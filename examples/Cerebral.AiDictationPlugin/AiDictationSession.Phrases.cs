using System.IO;
using Cerebral.Desktop.Plugins;

namespace Cerebral.AiDictationPlugin;

internal sealed partial class AiDictationSession
{
    private async Task<AiDictationCaptureResult> RunPhrasesAsync(Active active, string path,
        IResidentDictationProvider provider, CancellationToken token)
    {
        if (capture is not IAiDictationIncrementalCapture incremental)
            throw new InvalidOperationException("This capture adapter cannot keep listening between phrases. Use one-off recording.");
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(token);
        var queue = new DictationPhraseQueue();
        using var segmenter = new DictationPhraseSegmenter(active.Settings.SilenceWaitSeconds, queue.Add);
        active.KeepPhraseAudio = true;
        var decode = ConsumeAsync();
        Task<AiDictationCaptureResult>? recording = null;
        try
        {
            // The capture owns only PCM delivery and its bounded recovery WAV. It
            // never waits for recognition, final grammar or destination insertion.
            recording = incremental.CaptureIncrementalAsync(path, active.Settings.Capture with { AutoTranscribeOnSilence = false },
                active.Finish.Token, cancel.Token, level => { _ = dispatch(() =>
                {
                    if (Current(active)) Report(active.Target, DesktopTextEntryActivityState.Listening,
                        "Listening · phrases after pauses · select Stop to finish", true, "Stop AI", level);
                    return Task.CompletedTask;
                }); }, segmenter.Push);
            if (await Task.WhenAny(recording, decode).ConfigureAwait(false) == decode)
            {
                await decode.ConfigureAwait(false);
                throw new IOException("Phrase processing ended before capture completed. The run audio was retained.");
            }
            var captured = await recording.ConfigureAwait(false);
            active.CaptureEndedAtLimit = !active.Finish.IsCancellationRequested;
            active.Finish.Cancel();
            segmenter.Finish(); queue.Complete();
            await decode.ConfigureAwait(false);
            active.KeepPhraseAudio = reviewBeforeInsert || active.CaptureEndedAtLimit;
            return captured;
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            cancel.Cancel(); queue.Complete();
            if (recording is not null) try { await recording.ConfigureAwait(false); } catch (Exception cleanup) when (cleanup is not OutOfMemoryException) { }
            try { await decode.ConfigureAwait(false); } catch (Exception cleanup) when (cleanup is not OutOfMemoryException) { }
            throw;
        }

        async Task ConsumeAsync()
        {
            long expected = 0;
            while (await queue.TakeAsync(cancel.Token).ConfigureAwait(false) is { } phrase)
            {
                if (phrase.Sequence != checked(++expected)) throw new InvalidDataException("Completed phrases arrived out of order.");
                cancel.Token.ThrowIfCancellationRequested();
                var segmentPath = path + ".phrase-" + phrase.Sequence + ".wav";
                try
                {
                    DictationPcmWave.Write(segmentPath, phrase.Pcm);
                    var result = await provider.TranscribeAsync(new(segmentPath), active.Settings.Binding, cancel.Token).ConfigureAwait(false);
                    await ApplyRecognitionAsync(active, new(phrase.Sequence, result.Text, true), cancel.Token).ConfigureAwait(false);
                }
                finally { if (File.Exists(segmentPath)) File.Delete(segmentPath); }
            }
        }
    }
}
