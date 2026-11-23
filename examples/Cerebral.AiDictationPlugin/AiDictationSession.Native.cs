using System.IO;
using System.Threading.Channels;
using Cerebral.Desktop.Plugins;
using Cerebral.Dictation;
using Cerebral.Speech;

namespace Cerebral.AiDictationPlugin;

internal sealed partial class AiDictationSession
{
    private IResidentDictationProvider? residentProvider;
    private AiDictationSettings? residentSettings;
    private DictationDestinationRun? residentRun;
    private bool releaseResident;
    private Task retirement = Task.CompletedTask;
    private bool residentShutdownFailed;
    private Task shutdownCompletion = Task.CompletedTask;
    private DictationDraftComposition? streamingComposition;
    internal string PreviewText { get; private set; } = "";

    private ISpeechProvider GetProvider(AiDictationSettings selected)
    {
        if (residentProvider is not null)
        {
            if (residentSettings != selected) throw new InvalidOperationException("Dictation preferences changed. Stop and start again to use them.");
            return residentProvider;
        }
        var provider = providerFactory(selected);
        if (provider is IResidentDictationProvider resident) { residentProvider = resident; residentSettings = selected; residentRun = destinationRun; }
        return provider;
    }
    private async Task ReleaseResidentAsync(DictationDestinationRun? expectedRun = null)
    {
        IResidentDictationProvider? prior = null;
        await dispatch(() =>
        {
            if (expectedRun is not null && !ReferenceEquals(residentRun, expectedRun)) return Task.CompletedTask;
            prior = residentProvider; residentProvider = null; residentSettings = null; residentRun = null; return Task.CompletedTask;
        });
        if (prior is not null)
        {
            try { await prior.DisposeAsync().ConfigureAwait(false); }
            finally { shutdownCompletion = prior.ShutdownCompletion; }
        }
    }
    private void QueueResidentRelease() => retirement = ReleaseAndReportAsync();
    private async Task ReleaseAndReportAsync()
    {
        try { await ReleaseResidentAsync().ConfigureAwait(false); }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            await dispatch(() =>
            {
                LastState = DesktopTextEntryActivityState.Error;
                LastMessage = "The local speech helper has not confirmed shutdown. Recording stays disabled until its actual exit is confirmed.";
                if (!disposed) try { host.ReportStatus(LastMessage); } catch (ObjectDisposedException) { }
                return Task.CompletedTask;
            });
            residentShutdownFailed = true;
        }
    }
    internal DictationDestinationRun? DestinationIdentity => destinationRun;
    internal async Task EndRunAsync(DictationDestinationRun? expectedRun)
    {
        if (expectedRun is null || !ReferenceEquals(destinationRun, expectedRun)) return;
        releaseResident = true;
        if (current is { } active) await active.Work;
        await ReleaseResidentAsync(expectedRun);
    }

    private Task PrepareCompositionAsync(Active active) => dispatch(() =>
    {
        if (!Current(active)) throw new OperationCanceledException(active.Cancel.Token);
        if (host is IDesktopDefaultTextEntryHost main && ReferenceEquals(main.DefaultTextEntry, active.Target))
            streamingComposition ??= DictationDraftComposition.CreateMain(host, active.Target, () => !disposed &&
                ReferenceEquals(destinationRun, active.Run) && active.Run.Interrupted is null &&
                (targetCurrent is null || targetCurrent(active.Target)));
        return Task.CompletedTask;
    });

    private async Task<AiDictationCaptureResult> RunIncrementalAsync(Active active, string path,
        IResidentDictationProvider provider, CancellationToken token)
    {
        if (capture is not IAiDictationIncrementalCapture incremental)
            throw new InvalidOperationException("This capture adapter cannot provide live audio. Use one-off recording.");
        // A full queue stops capture explicitly; never silently drop/reorder audio.
        var audio = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(64) { SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.Wait });
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(token);
        var decode = provider.RecognizeStreamAsync(audio.Reader, result => ApplyRecognitionAsync(active, result, cancel.Token), cancel.Token);
        Task<AiDictationCaptureResult>? recording = null;
        try
        {
            recording = incremental.CaptureIncrementalAsync(path, active.Settings.Capture, active.Finish.Token, cancel.Token,
                level => { _ = dispatch(() =>
                {
                    if (Current(active)) Report(active.Target, DesktopTextEntryActivityState.Listening, "Listening · live words · select Stop to finish", true, "Stop AI", level);
                    return Task.CompletedTask;
                }); },
                chunk => { DictationNativeProtocol.CheckPcm(chunk); if (!audio.Writer.TryWrite(chunk)) throw new IOException("Live speech processing fell behind. Recording stopped; inspect the pending words before starting again."); });
            var first = await Task.WhenAny(recording, decode).ConfigureAwait(false);
            if (first == decode)
            {
                await decode.ConfigureAwait(false);
                throw new IOException("The live speech decoder ended before the recording completed.");
            }
            var captured = await recording.ConfigureAwait(false);
            audio.Writer.TryComplete();
            await decode.ConfigureAwait(false);
            return captured;
        }
        catch (Exception primary) when (primary is not OutOfMemoryException)
        {
            cancel.Cancel(); audio.Writer.TryComplete(primary);
            if (recording is not null) try { await recording.ConfigureAwait(false); } catch (Exception cleanup) when (cleanup is not OutOfMemoryException) { }
            try { await decode.ConfigureAwait(false); } catch (Exception cleanup) when (cleanup is not OutOfMemoryException) { }
            throw;
        }
    }

    private async Task ApplyRecognitionAsync(Active active, DictationRecognition result, CancellationToken token)
    {
        if (result.Sequence <= active.NativeSequence || result.Text is null || result.Text.Length > DictationReview.MaximumCharacters)
            throw new InvalidDataException("An old or oversized recognition event was refused.");
        active.NativeSequence = result.Sequence; active.NativePending = result.Text;
        var recognized = result.Final && correctSegment is not null && !string.IsNullOrWhiteSpace(result.Text)
            ? await correctSegment(active.Settings, result.Text, token).ConfigureAwait(false) : result.Text;
        token.ThrowIfCancellationRequested();
        await dispatch(async () =>
        {
            if (!Owned(active)) throw new OperationCanceledException(token);
            if (!Current(active)) active.Run.Interrupt("The captured destination is no longer current.");
            if (active.Run.Interrupted is { } interruption)
            { streamingComposition?.Interrupt(interruption); throw new InvalidOperationException(interruption); }
            var preview = DictatedTextInsertion.JoinSegments(active.NativeFinals, recognized);
            if (preview.Length > DictationReview.MaximumCharacters) throw new InvalidDataException("The live transcript preview is full. Stop before recording more.");
            PreviewText = preview;
            if (streamingComposition is { } composition)
            {
                var applied = await composition.ApplyAsync(result.Sequence, recognized, result.Final, token);
                if (applied.State != DesktopOperationState.Completed)
                    throw new InvalidOperationException(applied.Message ?? "The Main draft changed. Pending words were retained.");
                active.NativeInserted |= !string.IsNullOrWhiteSpace(recognized);
            }
            else if (result.Final && !string.IsNullOrWhiteSpace(recognized))
            {
                // Generic/Windows targets have no partial-range CAS. Only a final
                // phrase can use their existing checked insertion/review contract.
                Review.Append(active.Target, active.Run.Expected, recognized);
                if (!reviewBeforeInsert)
                {
                    var applied = await active.Run.InsertPhraseAsync(Review, token);
                    if (applied.State != DesktopOperationState.Completed) throw new InvalidOperationException(applied.Message);
                    active.NativeInserted = true;
                }
            }
            if (result.Final) { active.NativeFinals = preview; active.NativePending = ""; }
            Report(active.Target, DesktopTextEntryActivityState.Listening, active.Settings.LiveWordsWhileSpeaking
                ? "Listening · live words · select Stop to finish" : "Listening · phrases after pauses · select Stop to finish", true, "Stop AI");
        });
    }
    private Task RetainNativePendingAsync(Active active) => dispatch(() =>
    {
        if (!disposed && !active.DiscardPending && !string.IsNullOrWhiteSpace(active.NativePending))
            Review.RetainForInspection(active.Target, active.Insertion, active.NativePending,
                streamingComposition?.HasWritten == true || Review.InsertionUnconfirmed);
        return Task.CompletedTask;
    });
}
