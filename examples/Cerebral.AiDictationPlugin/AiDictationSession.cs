using System.IO;
using Cerebral.Desktop.Plugins;
using Cerebral.Speech;
using Cerebral.Dictation;

namespace Cerebral.AiDictationPlugin;

/// <summary>All target/binding/state access is dispatched to the desktop owner. Capture and inference run off its UI.</summary>
internal sealed partial class AiDictationSession(
    IDesktopPluginHost host,
    string captureDirectory,
    Func<AiDictationSettings> settings,
    Func<AiDictationSettings, ISpeechProvider> providerFactory,
    IAiDictationCapture capture,
    Func<Func<Task>, Task> dispatch,
    Action<DesktopTextEntryActivity, double?> presentation,
    Func<IDesktopTextEntry, bool>? targetCurrent = null,
    Func<AiDictationSettings, string, CancellationToken, Task<string>>? correctSegment = null,
    bool reviewBeforeInsert = false) : IDisposable
{
    internal const string PluginId = "pegasus.ai-dictation";
    internal const string ActionId = "ai-dictate";
    private readonly CancellationTokenSource lifetime = new();
    private Active? current;
    private bool disposed;
    private long startVersion;
    private DictationDestinationRun? destinationRun;
    private bool nextOneOff;
    private string? retainedAudioPath;
    private bool pendingPhraseAudio;
    internal string? RetainedAudioPath => retainedAudioPath;
    internal bool HasPendingPhraseAudio => pendingPhraseAudio;
    internal bool ContinuousAutomatic { get; private set; }
    internal bool IsOneOff => current?.OneOff == true;
    internal void BeginOneOff(IDesktopTextEntry target)
    { BeginDestinationRun(target); nextOneOff = true; }
    internal void ClearRetainedAudio()
    {
        if (IsActive) throw new InvalidOperationException("Wait for the recording to finish before discarding its audio.");
        if (retainedAudioPath is not { } path) return;
        File.Delete(path); retainedAudioPath = null; pendingPhraseAudio = false;
    }
    internal bool DestinationWriting => destinationRun?.Writing == true || streamingComposition?.Writing == true;
    internal bool HasDestinationRun => destinationRun is not null;
    internal void BeginDestinationRun(IDesktopTextEntry target)
    {
        if (IsActive || Review.HasText || Review.InsertionUnconfirmed || pendingPhraseAudio)
            throw new InvalidOperationException("Stop dictation and review or Discard the pending transcript / run audio before starting again.");
        ClearRetainedAudio(); nextOneOff = false; streamingComposition = null; PreviewText = "";
        releaseResident = true;
        var binding = host.GetConversation().Binding;
        long? ReadRevision()
        {
            if (host is not IDesktopDefaultTextEntryHost composer || !ReferenceEquals(composer.DefaultTextEntry, target)) return null;
            var draft = host.GetDraft();
            if (draft.Binding != binding || draft.Text != target.Text)
                throw new InvalidOperationException("The Main prompt binding or text changed.");
            return draft.Revision;
        }
        destinationRun = new(target, DictatedTextInsertion.Capture(target), ReadRevision, ReadRevision());
    }
    internal void InterruptDestination(string reason)
    {
        destinationRun?.Interrupt(reason);
        streamingComposition?.Interrupt(reason);
        FinishCurrent();
    }
    internal Task Completion => current?.Work ?? retirement;
    internal bool IsActive => current is not null;
    internal bool IsCapturing => current is { } active && !active.Finish.IsCancellationRequested && !active.Cancel.IsCancellationRequested;
    internal bool IsProcessing => current is not null && !IsCapturing;
    internal bool? CaptureAutomatic => current?.Settings.AutoTranscribeOnSilence;
    internal void FinishCurrent()
    {
        releaseResident = true;
        if (destinationRun is not null) destinationRun.StopRequested = true;
        if (current is { } active && !active.Cancel.IsCancellationRequested && !active.Finish.IsCancellationRequested)
            active.Finish.Cancel();
    }
    internal DesktopTextEntryActivityState LastState { get; private set; } = DesktopTextEntryActivityState.Idle;
    internal string LastMessage { get; private set; } = "";
    internal bool LastInsertionSucceeded { get; private set; }
    internal DictationReview Review { get; } = new();
    internal async Task<DesktopOperationResult> AcceptReviewAsync(CancellationToken token)
    {
        if (IsActive) return new(DesktopOperationState.Rejected, "Stop recording and wait for transcription before accepting the review.");
        if (Review.Target is not { } target || targetCurrent is not null && !targetCurrent(target))
            return new(DesktopOperationState.Rejected, "Select the intended destination again. The review was preserved.");
        var result = await Review.AcceptAsync(token);
        LastInsertionSucceeded = result.State == DesktopOperationState.Completed;
        LastMessage = result.Message ?? "Dictation review inserted. Nothing was submitted.";
        return result;
    }

    // Called on the host dispatcher, just like the text-entry action contract.
    public Task ToggleAsync(IDesktopTextEntry target, CancellationToken ct) => ChangeRecordingAsync(target, ct, false);
    internal Task ResumeAsync(IDesktopTextEntry target, CancellationToken ct) => ChangeRecordingAsync(target, ct, true);

    private async Task ChangeRecordingAsync(IDesktopTextEntry target, CancellationToken ct, bool freshSegment)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ct.ThrowIfCancellationRequested();
        var version = ++startVersion;
        if (current is { } old)
        {
            if (!freshSegment && ReferenceEquals(old.Target, target) && !old.Finish.IsCancellationRequested && !old.Cancel.IsCancellationRequested)
            {
                FinishCurrent();
                return;
            }
            old.Cancel.Cancel();
            await old.Work.WaitAsync(TimeSpan.FromSeconds(12));
            ct.ThrowIfCancellationRequested();
            ObjectDisposedException.ThrowIf(disposed, this);
            if (version != startVersion) throw new OperationCanceledException("This recording request was superseded.");
            if (!freshSegment && ReferenceEquals(old.Target, target)) return;
        }
        if (targetCurrent is not null && !targetCurrent(target))
            throw new OperationCanceledException("The original editor is no longer current.");
        if (destinationRun is null || destinationRun.StopRequested || !ReferenceEquals(destinationRun.Target, target)) BeginDestinationRun(target);
        await retirement;
        if (residentShutdownFailed)
        {
            if (!shutdownCompletion.IsCompletedSuccessfully) throw new InvalidOperationException("The previous speech helper is still settling. Recording remains disabled; no audio was retried.");
            residentShutdownFailed = false; // This explicit new Start follows confirmed exit, never an automatic retry.
        }
        if (releaseResident) await ReleaseResidentAsync();
        releaseResident = false;
        if (!(streamingComposition?.MatchesCurrent() ?? destinationRun!.Matches()))
            throw new InvalidOperationException("The destination changed between phrases. Press Start again after checking its cursor.");
        var selected = settings();
        if (nextOneOff) selected = selected with { AutoTranscribeOnSilence = false };
        // One-off recording/playback remains usable even if AI is unavailable. Its
        // provider is admitted after capture; automatic dictation preflights first.
        var provider = nextOneOff ? null : GetProvider(selected);
        var availability = provider is IResidentDictationProvider resident && selected.AutoTranscribeOnSilence && selected.LiveWordsWhileSpeaking
            ? resident.InspectStreaming() : provider?.Inspect(selected.Binding);
        if (availability is { Available: false })
        {
            LastState = DesktopTextEntryActivityState.Error;
            Report(target, DesktopTextEntryActivityState.Error, availability.Reason!, false, "🎙✦ AI Dictate");
            return;
        }
        var available = target is IDesktopTextEntryLifetime entryLifetime ? entryLifetime.AvailabilityToken : CancellationToken.None;
        available.ThrowIfCancellationRequested();
        if (provider is IResidentDictationProvider && selected.AutoTranscribeOnSilence && host is IDesktopDefaultTextEntryHost main &&
            ReferenceEquals(main.DefaultTextEntry, target) && !main.SupportsDraftPresentation)
            throw new InvalidOperationException("This Main host needs the protected live-text update. One-off recording remains available.");
        var active = new Active(target, host.GetConversation().Binding, DictatedTextInsertion.Capture(target), destinationRun!, selected, provider, nextOneOff,
            CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token, ct, available));
        ContinuousAutomatic = !active.OneOff && selected.AutoTranscribeOnSilence && !selected.LiveWordsWhileSpeaking && provider is IResidentDictationProvider;
        current = active;
        active.Work = RunAsync(active);
    }

    public void Cancel(bool discardReview = false)
    {
        startVersion++; releaseResident = true; streamingComposition?.Interrupt("Dictation was cancelled.");
        if (current is { } active) { active.DiscardPending |= discardReview; active.Cancel.Cancel(); } else QueueResidentRelease();
        if (discardReview) { Review.Clear(); PreviewText = ""; }
    }

    internal async Task<DesktopOperationResult> PrepareSubmissionAsync(IDesktopTextEntry target,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (residentShutdownFailed && !shutdownCompletion.IsCompletedSuccessfully)
            return new(DesktopOperationState.Rejected, "The previous speech helper is still settling. Recording remains disabled; no audio was retried.");
        var active = current;
        if (active is not null && ReferenceEquals(active.Target, target))
        {
            // Send flushes the last phrase and joins checked insertion and helper
            // shutdown. Cancelling the recording here would strand pending audio.
            FinishCurrent();
            await active.Work.WaitAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (LastState == DesktopTextEntryActivityState.Error)
                return new(DesktopOperationState.Rejected, LastMessage);
        }
        if (!pendingPhraseAudio && (current is null || !ReferenceEquals(current.Target, target)) &&
            !Review.InsertionUnconfirmed && (!Review.HasText || !ReferenceEquals(Review.Target, target)))
            return new(DesktopOperationState.Completed);
        return new(DesktopOperationState.Rejected, "Recover or discard the pending phrase or run audio before sending. Dictation never submits a message.");
    }

    private async Task RunAsync(Active active)
    {
        var root = Path.GetFullPath(captureDirectory);
        var path = Path.Combine(root, Guid.NewGuid().ToString("N") + ".wav");
        var token = active.Cancel.Token;
        var state = DesktopTextEntryActivityState.Idle;
        var message = "AI dictation stopped.";
        var insertedText = false;
        var capturedAudio = false;
        try
        {
            await dispatch(() =>
            {
                if (!Current(active)) throw new OperationCanceledException(token);
                Directory.CreateDirectory(root);
                Report(active.Target, DesktopTextEntryActivityState.Starting, "Starting the microphone…", true, "Stop AI");
                return Task.CompletedTask;
            });
            if (!active.OneOff && active.Settings.AutoTranscribeOnSilence && active.Provider is IResidentDictationProvider resident)
            {
                await resident.PrepareAsync(active.Settings.LiveWordsWhileSpeaking, token).ConfigureAwait(false);
                if (active.Finish.IsCancellationRequested) return; // Stop during model preparation never starts the microphone.
                await PrepareCompositionAsync(active).ConfigureAwait(false);
                var streamed = active.Settings.LiveWordsWhileSpeaking
                    ? await RunIncrementalAsync(active, path, resident, token).ConfigureAwait(false)
                    : await RunPhrasesAsync(active, path, resident, token).ConfigureAwait(false);
                capturedAudio = streamed.DurationSeconds >= .2;
                active.Finish.Cancel();
                insertedText = active.NativeInserted;
                state = DesktopTextEntryActivityState.Result;
                message = insertedText ? "Recognized phrases added. Nothing was sent." : "No speech was recognized.";
                if (ContinuousAutomatic && active.CaptureEndedAtLimit)
                    message += " The two-minute recording limit was reached. Review the run audio and earlier inserted text, then Discard the recording before using Start explicitly to continue.";
                return;
            }
            var captured = await capture.CaptureAsync(path, active.Settings.Capture, active.Finish.Token, token, value =>
            {
                _ = dispatch(() =>
                {
                    if (Current(active)) Report(active.Target, DesktopTextEntryActivityState.Listening,
                        $"Listening · processed input {Math.Round(value * 100):0}%{(value >= .99 ? " · clipping" : "")} · select Stop AI to transcribe", true, "Stop AI", value);
                    return Task.CompletedTask;
                });
            }).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            // The capture may stop itself at the duration cap. The next click now means cancel, not finish again.
            active.Finish.Cancel();
            if (captured.DurationSeconds < .2) { message = "The recording was too short. Try again and speak before selecting Stop AI."; return; }
            capturedAudio = true;
            var normalization = captured.Normalization;
            var normalizationNote = normalization.Applied ? $" · normalized +{normalization.GainDb:0.0} dB" : "";
            await dispatch(() =>
            {
                if (!Owned(active)) throw new OperationCanceledException(token);
                if (active.OneOff) retainedAudioPath = path;
                Report(active.Target, DesktopTextEntryActivityState.Stopping, $"Transcribing the completed phrase locally{normalizationNote}…", true, "Stop AI");
                return Task.CompletedTask;
            });
            ISpeechProvider? provider = active.Provider;
            if (provider is null) await dispatch(() => { provider = GetProvider(active.Settings); return Task.CompletedTask; });
            var availability = provider!.Inspect(active.Settings.Binding);
            if (!availability.Available) throw new SpeechProviderException("unavailable", availability.Reason ?? "AI transcription is unavailable. The recording is available for Play.");
            var result = await provider.TranscribeAsync(new(path), active.Settings.Binding, token).ConfigureAwait(false);
            var recognized = correctSegment is not null && !string.IsNullOrWhiteSpace(result.Text)
                ? await correctSegment(active.Settings, result.Text, token).ConfigureAwait(false) : result.Text;
            token.ThrowIfCancellationRequested();
            await dispatch(async () =>
            {
                if (!Owned(active)) throw new OperationCanceledException(token);
                if (string.IsNullOrWhiteSpace(result.Text)) { state = DesktopTextEntryActivityState.Result; message = "No speech was recognized."; return; }
                Review.Append(active.Target, active.Insertion, recognized);
                if (!Current(active) || active.Target is not WindowsDictationTarget && host.GetConversation().Binding != active.Binding)
                    active.Run.Interrupt("The original destination is no longer available. The phrase was retained.");
                if (active.OneOff || reviewBeforeInsert)
                { state = DesktopTextEntryActivityState.Result; message = "Recording ready. Play it, edit the transcript, then Insert when ready. Nothing was sent."; return; }
                var applied = await active.Run.InsertPhraseAsync(Review, token);
                insertedText = applied.State == DesktopOperationState.Completed;
                state = insertedText ? DesktopTextEntryActivityState.Result : DesktopTextEntryActivityState.Error;
                message = insertedText ? "Phrase added to the destination. Edit it there before sending; nothing was submitted."
                    : applied.Message ?? "Dictation stopped. The pending phrase is available in Settings for recovery.";
            });
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        { message = "AI dictation cancelled."; await RetainNativePendingAsync(active).ConfigureAwait(false); }
        catch (Exception ex)
        {
            state = DesktopTextEntryActivityState.Error;
            message = ex is SpeechProviderException speech ? speech.Message : "AI dictation failed: " + ex.Message;
            await RetainNativePendingAsync(active).ConfigureAwait(false);
        }
        finally
        {
            if (releaseResident || ContinuousAutomatic || active.OneOff || !active.Settings.AutoTranscribeOnSilence || state == DesktopTextEntryActivityState.Error || active.Cancel.IsCancellationRequested || disposed)
                try { await ReleaseResidentAsync(active.Run).ConfigureAwait(false); }
                catch (Exception error) when (error is not OutOfMemoryException)
                { residentShutdownFailed = true; state = DesktopTextEntryActivityState.Error; message = "The local speech helper has not confirmed shutdown. Recording remains disabled until its actual exit is confirmed."; }
            var retainPhrase = active.KeepPhraseAudio && !active.DiscardPending && !disposed && File.Exists(path) && new FileInfo(path).Length > 44;
            if (retainPhrase)
            {
                message += " The run audio is retained for Play; it includes any earlier inserted phrases. Inspect the destination, then recover or Discard before starting again.";
            }
            var retain = retainPhrase || active.OneOff && capturedAudio && retainedAudioPath == path && !active.Cancel.IsCancellationRequested && !disposed;
            try { if (!retain && File.Exists(path)) File.Delete(path); }
            catch (IOException) { state = DesktopTextEntryActivityState.Error; message = "AI dictation stopped, but its temporary recording could not be removed."; }
            catch (UnauthorizedAccessException) { state = DesktopTextEntryActivityState.Error; message = "AI dictation stopped, but its temporary recording could not be removed."; }
            try
            {
                await dispatch(() =>
                {
                    if (ReferenceEquals(current, active))
                    {
                        current = null;
                        if (retainPhrase) { retainedAudioPath = path; pendingPhraseAudio = true; }
                        if (!retain && retainedAudioPath == path) retainedAudioPath = null;
                        LastState = state;
                        LastMessage = message;
                        LastInsertionSucceeded = insertedText;
                        if (!active.Cancel.IsCancellationRequested) Report(active.Target, state, message, false, "🎙✦ AI Dictate");
                    }
                    return Task.CompletedTask;
                });
            }
            finally { active.Cancel.Dispose(); active.Finish.Dispose(); }
        }
    }

    private bool Owned(Active active) => !disposed && ReferenceEquals(current, active) && !active.Cancel.IsCancellationRequested;
    private bool Current(Active active) => Owned(active)
        && (active.Target is not IDesktopTextEntryLifetime entry || !entry.AvailabilityToken.IsCancellationRequested)
        && (targetCurrent is null || targetCurrent(active.Target));

    private void Report(IDesktopTextEntry target, DesktopTextEntryActivityState state, string text, bool active, string button, double? level = null)
    {
        if (disposed || lifetime.IsCancellationRequested || targetCurrent is not null && !targetCurrent(target)) return;
        var activity = new DesktopTextEntryActivity(PluginId, ActionId, state, text, active, button) { InputLevel = level };
        if (target is IDesktopTextEntryActivitySink sink) sink.ReportActivity(activity);
        presentation(activity, level);
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        lifetime.Cancel();
        releaseResident = true;
        if (!IsActive) QueueResidentRelease();
        // The running operation retains its linked token until native capture/process cleanup settles.
        try { if (!IsActive) ClearRetainedAudio(); }
        finally { lifetime.Dispose(); }
    }

    private sealed class Active(IDesktopTextEntry target, DesktopConversationBinding binding, DictatedTextInsertion insertion, DictationDestinationRun run, AiDictationSettings settings,
        ISpeechProvider? provider, bool oneOff, CancellationTokenSource cancel)
    {
        public IDesktopTextEntry Target { get; } = target;
        public DesktopConversationBinding Binding { get; } = binding;
        public DictatedTextInsertion Insertion { get; } = insertion;
        public DictationDestinationRun Run { get; } = run;
        public AiDictationSettings Settings { get; } = settings;
        public ISpeechProvider? Provider { get; } = provider;
        public bool OneOff { get; } = oneOff;
        public CancellationTokenSource Cancel { get; } = cancel;
        public CancellationTokenSource Finish { get; } = new();
        public Task Work { get; set; } = Task.CompletedTask;
        public string NativePending { get; set; } = "";
        public string NativeFinals { get; set; } = "";
        public long NativeSequence { get; set; }
        public bool NativeInserted { get; set; }
        public bool KeepPhraseAudio { get; set; }
        public bool DiscardPending { get; set; }
        public bool CaptureEndedAtLimit { get; set; }
    }
}
