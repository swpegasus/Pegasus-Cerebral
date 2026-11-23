using System.Buffers.Binary;
using System.IO;
using System.Threading.Channels;
using System.Windows.Threading;
using Cerebral.AiDictationPlugin;
using Cerebral.Desktop.Plugins;
using Cerebral.Speech;

internal static partial class Program
{
    private static async Task<int> RunResidentDictationChecksAsync(Dispatcher dispatcher)
    {
        var checks = 0;
        void Check(bool value, string label) { if (!value) throw new InvalidOperationException(label); checks++; }
        Func<Func<Task>, Task> dispatch = action => dispatcher.InvokeAsync(action).Task.Unwrap();
        var root = Path.Combine(Path.GetTempPath(), "cerebral-resident-fake-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var entry = new NativeEntry(dispatcher); entry.Box.Text = "Keep. Tail"; entry.Box.Select(5, 0);
            long revision = 1; entry.Box.TextChanged += (_, _) => revision++;
            var host = new TestHost { DefaultTextEntry = entry, SupportsDraftPresentation = true };
            host.DraftReader = () => new(host.Binding, revision, entry.Text);
            using var effect = new Cerebral.Orchestrator.ComposerDraftPresentation(entry.Box);
            host.DraftWriter = (update, _) => Task.FromResult(update.Binding == host.Binding && update.ExpectedRevision == revision && update.Presentation is { } presentation
                ? effect.Apply(update.Text, presentation) : new DesktopOperationResult(DesktopOperationState.Rejected));
            var capture = new ResidentFakeCapture(); var provider = new ResidentFakeProvider(); var factories = 0; var grammarCalls = 0;
            using (var session = new AiDictationSession(host, Path.Combine(root, "main"), () => new() { AutoTranscribeOnSilence = true, LiveWordsWhileSpeaking = true },
                _ => { factories++; return provider; }, capture, dispatch, (_, _) => { }, _ => true,
                (_, text, _) => { grammarCalls++; return Task.FromResult(text == "one phrase" ? "One phrase." : "Another."); }))
            {
                session.BeginDestinationRun(entry); await session.ToggleAsync(entry, default); await WaitAsync(() => capture.Calls == 1);
                capture.Emit(1); await WaitAsync(() => entry.Text == "Keep. won Tail");
                Check(grammarCalls == 0 && session.PreviewText == "won", "Real fake-provider partial events reach Main and floating preview without final grammar.");
                capture.Emit(2); await WaitAsync(() => entry.Text == "Keep. one phrase Tail");
                capture.Emit(3); await WaitAsync(() => entry.Text == "Keep. One phrase. Tail");
                Check(grammarCalls == 1 && !session.Review.HasText && host.Submissions == 0, "Only final prose is corrected and directly applied; no conversation submission.");
                capture.Complete(); await session.Completion.WaitAsync(TimeSpan.FromSeconds(5));
                Check(provider.Disposals == 0 && provider.Loads == 1, "A natural pause keeps one CPU model resident between captures.");
                await session.ResumeAsync(entry, default); await WaitAsync(() => capture.Calls == 2);
                capture.Emit(4); await WaitAsync(() => entry.Text == "Keep. One phrase. another Tail");
                session.FinishCurrent(); session.FinishCurrent(); await session.Completion.WaitAsync(TimeSpan.FromSeconds(5));
                Check(entry.Text == "Keep. One phrase. Another. Tail" && factories == 1 && provider.Loads == 1 && provider.Streams == 2 && provider.Disposals == 1 && grammarCalls == 2,
                    "Two captures reuse one helper; repeated Stop flushes one final phrase and closes it once.");
            }
            var conflictCapture = new ResidentFakeCapture(); var conflictProvider = new ResidentFakeProvider();
            entry.Box.Text = "Keep."; entry.Box.Select(5, 0);
            using (var session = new AiDictationSession(host, Path.Combine(root, "conflict"), () => new() { AutoTranscribeOnSilence = true, LiveWordsWhileSpeaking = true },
                _ => conflictProvider, conflictCapture, dispatch, (_, _) => { }, _ => true))
            {
                session.BeginDestinationRun(entry); await session.ToggleAsync(entry, default); await WaitAsync(() => conflictCapture.Calls == 1);
                conflictCapture.Emit(1); await WaitAsync(() => entry.Text == "Keep. won");
                entry.Box.AppendText(" human edit"); var userText = entry.Text;
                conflictCapture.Emit(3); await session.Completion.WaitAsync(TimeSpan.FromSeconds(5));
                Check(session.LastState == DesktopTextEntryActivityState.Error && entry.Text == userText && session.Review.InsertionUnconfirmed && conflictProvider.Disposals == 1,
                    "A real draft revision conflict stops decoding/capture, preserves user text and blocks replay of already visible tentative words.");
            }
            var missingCapture = new ResidentFakeCapture(); var missing = new ResidentFakeProvider { MissingStreaming = true };
            var manual = new ResidentFakeProvider(); var missingFactoryCalls = 0;
            using (var session = new AiDictationSession(host, Path.Combine(root, "missing"), () => new() { AutoTranscribeOnSilence = true, LiveWordsWhileSpeaking = true },
                _ => ++missingFactoryCalls == 1 ? missing : manual, missingCapture, dispatch, (_, _) => { }, _ => true))
            {
                session.BeginDestinationRun(entry); await session.ToggleAsync(entry, default);
                Check(missingCapture.Calls == 0 && missing.Loads == 0 && session.LastState == DesktopTextEntryActivityState.Error,
                    "Missing streaming asset refuses automatic recording before microphone/helper startup.");
                session.BeginOneOff(entry); await session.ResumeAsync(entry, default); await WaitAsync(() => missingCapture.Calls == 1);
                session.FinishCurrent(); await session.Completion.WaitAsync(TimeSpan.FromSeconds(5));
                Check(session.Review.Text == "manual phrase" && missing.Streams == 0 && manual.Transcriptions == 1 && File.Exists(session.RetainedAudioPath),
                    "The same unavailable-streaming configuration still records and transcribes a one-off offline clip for review/playback.");
            }
            var preparingCapture = new ResidentFakeCapture(); var preparing = new ResidentFakeProvider { PrepareGate = new(TaskCreationOptions.RunContinuationsAsynchronously) };
            using (var session = new AiDictationSession(host, Path.Combine(root, "preparing"), () => new() { AutoTranscribeOnSilence = true, LiveWordsWhileSpeaking = true },
                _ => preparing, preparingCapture, dispatch, (_, _) => { }, _ => true))
            {
                session.BeginDestinationRun(entry); await session.ToggleAsync(entry, default);
                await WaitAsync(() => preparing.PrepareCalls > 0); session.FinishCurrent(); preparing.PrepareGate.SetResult();
                await session.Completion.WaitAsync(TimeSpan.FromSeconds(5));
                Check(preparingCapture.Calls == 0 && preparing.Disposals == 1, "Stop during model preparation never starts capture and releases the helper.");
            }
            var failureCapture = new ResidentFakeCapture(); var failure = new ResidentFakeProvider();
            using (var session = new AiDictationSession(host, Path.Combine(root, "decode-failure"), () => new() { AutoTranscribeOnSilence = true, LiveWordsWhileSpeaking = true },
                _ => failure, failureCapture, dispatch, (_, _) => { }, _ => true))
            {
                session.BeginDestinationRun(entry); await session.ToggleAsync(entry, default); await WaitAsync(() => failureCapture.Calls == 1);
                failureCapture.Emit(9); await session.Completion.WaitAsync(TimeSpan.FromSeconds(5));
                Check(failureCapture.Cancelled == 1 && failure.Disposals == 1 && session.LastState == DesktopTextEntryActivityState.Error,
                    $"A decoder failure cancels capture, observes both tasks and closes the owned helper (capture cancellations={failureCapture.Cancelled}, disposals={failure.Disposals}, state={session.LastState}).");
            }
            using var frame = new MemoryStream();
            await DictationNativeProtocol.WriteAsync(frame, new DictationNativeRequest(1, 1, "audio", [1, 0]), default); frame.Position = 0;
            var decoded = await DictationNativeProtocol.ReadAsync<DictationNativeRequest>(frame, default);
            Check(decoded.Id == 1 && decoded.Pcm!.SequenceEqual(new byte[] { 1, 0 }), "A bounded private PCM frame round-trips without invoking native speech.");
            foreach (var size in new[] { -1, 0, DictationNativeProtocol.MaximumFrameBytes + 1 })
            {
                var header = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(header, size); using var invalid = new MemoryStream(header);
                var refused = false; try { await DictationNativeProtocol.ReadAsync<DictationNativeRequest>(invalid, default); } catch (InvalidDataException) { refused = true; }
                Check(refused, "Invalid/oversized frame refuses allocation: " + size);
            }
            foreach (var pcm in new[] { Array.Empty<byte>(), new byte[3], new byte[DictationNativeProtocol.MaximumPcmBytes + 2] })
            {
                var refused = false; try { DictationNativeProtocol.CheckPcm(pcm); } catch (InvalidDataException) { refused = true; }
                Check(refused, "Partial or oversized PCM never reaches native recognition.");
            }
        }
        finally
        {
            var resolved = Path.GetFullPath(root);
            if (!resolved.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase) ||
                !Path.GetFileName(resolved).StartsWith("cerebral-resident-fake-", StringComparison.Ordinal)) throw new IOException("Unsafe resident fixture cleanup.");
            if (Directory.Exists(resolved)) Directory.Delete(resolved, true);
        }
        return checks;
    }

    private sealed class ResidentFakeCapture : IAiDictationIncrementalCapture
    {
        internal int Calls, Cancelled;
        private Action<byte[]>? emit;
        private TaskCompletionSource? complete;
        internal void Emit(byte value) => emit!(new byte[] { value, 0 });
        internal void Complete() => complete!.TrySetResult();
        public Task<AiDictationCaptureResult> CaptureAsync(string path, AiDictationCaptureOptions options, CancellationToken finish, CancellationToken cancel, Action<double> level) =>
            CaptureIncrementalAsync(path, options, finish, cancel, level, _ => { });
        public async Task<AiDictationCaptureResult> CaptureIncrementalAsync(string path, AiDictationCaptureOptions options,
            CancellationToken finish, CancellationToken cancel, Action<double> level, Action<byte[]> pcm)
        {
            emit = pcm; complete = new(TaskCreationOptions.RunContinuationsAsynchronously); Calls++;
            try
            {
                await File.WriteAllTextAsync(path, "fake recorded fixture", cancel); level(.3);
                using var stopped = CancellationTokenSource.CreateLinkedTokenSource(finish, cancel);
                try { await complete.Task.WaitAsync(stopped.Token); }
                catch (OperationCanceledException) when (!cancel.IsCancellationRequested) { }
                cancel.ThrowIfCancellationRequested();
                return new(1, new(false, .3, .3, 0));
            }
            catch (OperationCanceledException) when (cancel.IsCancellationRequested)
            {
                // Cancellation can arrive during the asynchronous fixture write,
                // before the simulated microphone's wait has started.
                Interlocked.Increment(ref Cancelled); throw;
            }
        }
    }
    private sealed class ResidentFakeProvider : IResidentDictationProvider
    {
        internal int Loads, Streams, Disposals, PrepareCalls, Transcriptions;
        internal bool MissingStreaming;
        internal TaskCompletionSource? PrepareGate;
        private long sequence;
        private bool disposed;
        public SpeechProviderDescriptor Descriptor => new("resident-fake", "1", "deterministic speech fixture", true);
        public SpeechProviderAvailability Inspect(SpeechModelBinding model) => new(true, null);
        public SpeechProviderAvailability InspectStreaming() => new(!MissingStreaming, MissingStreaming ? "Fixture streaming asset missing; use one-off recording." : null);
        public async Task PrepareAsync(bool streaming, CancellationToken token)
        { ObjectDisposedException.ThrowIf(disposed, this); PrepareCalls++; if (PrepareGate is not null) await PrepareGate.Task.WaitAsync(token); if (Loads == 0) Loads++; }
        public async Task RecognizeStreamAsync(ChannelReader<byte[]> pcm, Func<DictationRecognition, Task> result, CancellationToken token)
        {
            ObjectDisposedException.ThrowIf(disposed, this); Streams++; string pending = "";
            await foreach (var chunk in pcm.ReadAllAsync(token))
            {
                var (text, final) = chunk[0] switch { 1 => ("won", false), 2 => ("one phrase", false), 3 => ("one phrase", true),
                    4 => ("another", false), _ => throw new IOException("Fixture decoder failure.") };
                await result(new(++sequence, text, final)); pending = final ? "" : text;
            }
            if (pending.Length != 0) await result(new(++sequence, pending, true));
        }
        public Task<SpeechTranscript> TranscribeAsync(SpeechAudioInput audio, SpeechModelBinding model, CancellationToken token)
        { ObjectDisposedException.ThrowIf(disposed, this); Transcriptions++; return Task.FromResult(new SpeechTranscript("manual phrase", 1, 1, Descriptor.Id, model.ModelId)); }
        public ValueTask DisposeAsync() { if (!disposed) { disposed = true; Disposals++; } return ValueTask.CompletedTask; }
    }
}
