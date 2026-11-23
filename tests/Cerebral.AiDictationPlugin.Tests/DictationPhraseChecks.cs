using System.Buffers.Binary;
using System.IO;
using System.Reflection;
using System.Threading.Channels;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;
using Cerebral.AiDictationPlugin;
using Cerebral.Desktop.Plugins;
using Cerebral.Speech;

internal static partial class Program
{
    private static async Task<int> RunPhraseChecksAsync(Dispatcher dispatcher)
    {
        var checks = 0;
        void Check(bool value, string label) { if (!value) throw new InvalidOperationException(label); checks++; }
        static bool Refuses(Action action) { try { action(); return false; } catch (IOException) { return true; } }
        var phrases = new List<DictationPcmPhrase>();
        using (var segmenter = new DictationPhraseSegmenter(.5, phrases.Add))
        {
            segmenter.Push(PhrasePcm(101)); segmenter.Push(PhrasePcm(102)); segmenter.Push(PhrasePcm(103));
            segmenter.Push(PhrasePcm(1001));
            for (var i = 0; i < 4; i++) segmenter.Push(PhrasePcm(0));
            Check(phrases.Count == 0, "A phrase is not published before the configured sample-based pause.");
            segmenter.Push(PhrasePcm(0));
            Check(phrases.Count == 1 && phrases[0].Sequence == 1 && phrases[0].Pcm.Length == 8 * 3200 &&
                BinaryPrimitives.ReadInt16LittleEndian(phrases[0].Pcm) == 102 &&
                BinaryPrimitives.ReadInt16LittleEndian(phrases[0].Pcm.AsSpan(3200)) == 103,
                "Exactly the most recent 200ms of silence precedes speech, in original sample order.");
            segmenter.Push(PhrasePcm(2001)); segmenter.Finish(); segmenter.Finish();
            Check(phrases.Count == 2 && phrases[1].Sequence == 2 && phrases[1].Pcm.Length == 3200 &&
                BinaryPrimitives.ReadInt16LittleEndian(phrases[1].Pcm) == 2001,
                "Stop flushes a final phrase once, without replaying the previous phrase's quiet tail.");
        }
        using (var longSpeech = new DictationPhraseSegmenter(.5, _ => throw new InvalidOperationException("No unsafe hard split may publish.")))
        {
            for (var i = 0; i < 300; i++) longSpeech.Push(PhrasePcm(1001));
            Check(Refuses(() => longSpeech.Push(PhrasePcm(1001))), "Thirty seconds without a complete pause stops instead of guessing a word split.");
        }
        var queue = new DictationPhraseQueue(); queue.Add(new(1, new byte[16000])); queue.Add(new(2, new byte[16000]));
        Check(Refuses(() => queue.Add(new(3, new byte[2]))), "The third waiting phrase is refused explicitly.");
        Check((await queue.TakeAsync(default))!.Sequence == 1 && (await queue.TakeAsync(default))!.Sequence == 2, "Waiting phrases retain FIFO ownership.");
        queue.Complete(); Check(await queue.TakeAsync(default) is null, "A completed empty queue settles without another decode.");
        var bytesQueue = new DictationPhraseQueue(); bytesQueue.Add(new(1, new byte[16 * 32000]));
        Check(Refuses(() => bytesQueue.Add(new(2, new byte[16 * 32000]))), "The waiting-audio byte budget is enforced independently of phrase count.");
        var mode = new AiDictationSettings { AutoTranscribeOnSilence = true, LiveWordsWhileSpeaking = true };
        Check(AiDictationSettingsPolicy.Decode(AiDictationSettingsPolicy.Encode(mode)) == mode && !new AiDictationSettings().LiveWordsWhileSpeaking,
            "Native live words have an explicit persisted opt-in; existing settings default to completed phrases.");

        var root = Path.Combine(Path.GetTempPath(), "cerebral-phrase-fake-" + Guid.NewGuid().ToString("N"));
        try
        {
            var grammarEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var grammarRelease = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            var grammarCalls = 0;
            await using (var f = new PhraseFixture(dispatcher, Path.Combine(root, "continuous"), correct: (_, words, token) =>
            {
                if (++grammarCalls != 1) return Task.FromResult(words);
                grammarEntered.SetResult(); return grammarRelease.Task.WaitAsync(token);
            }))
            {
                f.WriteGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
                await f.StartAsync(); f.Capture.Emit(PhrasePcm(100)); f.Capture.Emit(PhrasePcm(101)); f.Capture.Emit(PhrasePcm(102));
                EmitPhrase(f.Capture, 1001); await WaitAsync(() => f.Provider.Calls == 1);
                EmitPhrase(f.Capture, 2001);
                Check(f.Capture.Capturing && f.Capture.Calls == 1 && f.Provider.Calls == 1 && f.Entry.Text == "Keep. Tail",
                    "PCM capture continues while the first resident offline decode is delayed.");
                f.Provider.Reply(1, "first"); await grammarEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                EmitPhrase(f.Capture, 3001);
                Check(f.Capture.Capturing && f.Provider.Calls == 1 && grammarCalls == 1, "Final-only grammar does not block PCM capture or start concurrent decoders.");
                grammarRelease.SetResult("First."); await f.WriteEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                f.Capture.Emit(PhrasePcm(4001));
                Check(f.Capture.Capturing && f.Capture.Calls == 1 && f.Provider.Calls == 1, "A delayed Main insertion also leaves capture running.");
                f.WriteGate.SetResult(); await WaitAsync(() => f.Provider.Calls == 2); f.Provider.Reply(2, "second");
                await WaitAsync(() => f.Provider.Calls == 3); f.Provider.Reply(3, "third");
                await WaitAsync(() => f.Entry.Text == "Keep. First. second third Tail");
                f.Session.FinishCurrent(); f.Session.FinishCurrent();
                await WaitAsync(() => f.Provider.Calls == 4); f.Provider.Reply(4, "fourth");
                await f.Session.Completion.WaitAsync(TimeSpan.FromSeconds(5));
                Check(f.Entry.Text == "Keep. First. second third fourth Tail" && f.Writes == 4 && grammarCalls == 4 && f.Host.Submissions == 0,
                    "Completed phrases, final spacing/grammar and Stop's last phrase apply exactly once in capture order, without submitting.");
                Check(f.Provider.Loads == 1 && f.Provider.MaximumInFlight == 1 && f.Provider.PrepareModes.All(value => !value) && f.Provider.StreamingInspections == 0,
                    "Phrase mode uses one warm offline recognizer without consulting the absent streaming asset.");
                Check(f.Provider.Audio.Count == 4 && f.Provider.Audio[0].Length == 8 * 3200 && f.Provider.Audio[1].Length == 6 * 3200 &&
                    BinaryPrimitives.ReadInt16LittleEndian(f.Provider.Audio[1]) == 2001 && BinaryPrimitives.ReadInt16LittleEndian(f.Provider.Audio[2]) == 3001,
                    "The actual provider receives bounded nonoverlapping WAV phrases in order, including only the owned preroll.");
                Check(f.Provider.Disposals == 1 && !f.Capture.Capturing && f.Session.RetainedAudioPath is null &&
                    !Directory.EnumerateFiles(f.Directory, "*.wav*").Any(), "Successful Stop closes the helper once and removes owned capture/phrase files.");
            }
            await using (var f = new PhraseFixture(dispatcher, Path.Combine(root, "backlog")))
            {
                await f.StartAsync(); EmitPhrase(f.Capture, 1001); await WaitAsync(() => f.Provider.Calls == 1);
                EmitPhrase(f.Capture, 2001); EmitPhrase(f.Capture, 3001); EmitPhrase(f.Capture, 4001);
                await f.Session.Completion.WaitAsync(TimeSpan.FromSeconds(5));
                Check(f.Session.LastState == DesktopTextEntryActivityState.Error && f.Session.LastMessage.Contains("fell behind") && f.Entry.Text == "Keep. Tail" &&
                    f.Provider.Calls == 1 && f.Provider.Disposals == 1 && !f.Capture.Capturing,
                    "Backlog stops capture and the delayed decode without dropping queued phrases into later insertions.");
                Check(f.ValidRetainedWave() && f.Session.HasPendingPhraseAudio && f.Session.LastMessage.Contains("earlier inserted"),
                    "The whole bounded captured run, including the refused phrase, remains playable with an earlier-text warning.");
                var refused = false; try { f.Session.BeginDestinationRun(f.Entry); } catch (InvalidOperationException) { refused = true; }
                Check(refused && (await f.Session.PrepareSubmissionAsync(f.Entry, default)).State == DesktopOperationState.Rejected,
                    "Pending audio blocks implicit replacement and submission until deliberate recovery/discard.");
                f.Session.ClearRetainedAudio(); Check(!f.Session.HasPendingPhraseAudio && f.Session.RetainedAudioPath is null, "Explicit Discard removes the pending run clip and its gate.");
            }
            await using (var f = new PhraseFixture(dispatcher, Path.Combine(root, "long")))
            {
                await f.StartAsync(); for (var i = 0; i < 301; i++) f.Capture.Emit(PhrasePcm(1001));
                await f.Session.Completion.WaitAsync(TimeSpan.FromSeconds(5));
                Check(f.Session.LastState == DesktopTextEntryActivityState.Error && f.Provider.Calls == 0 && f.ValidRetainedWave() && f.Entry.Text == "Keep. Tail",
                    "A long unbroken utterance is retained without an unsafe forced decode, overlap guess or silent deletion.");
            }
            foreach (var conflict in new[] { "edit", "caret", "binding", "target" })
            {
                await using var f = new PhraseFixture(dispatcher, Path.Combine(root, conflict));
                await f.StartAsync(); EmitPhrase(f.Capture, 1001); await WaitAsync(() => f.Provider.Calls == 1); EmitPhrase(f.Capture, 2001);
                if (conflict == "edit") f.Entry.Box.AppendText(" human edit");
                if (conflict == "caret") f.Entry.Box.Select(0, 0);
                if (conflict == "binding") f.Host.Binding = new("changed-owner", null);
                if (conflict == "target") { f.Current = false; f.Session.InterruptDestination("The original target changed."); }
                var userText = f.Entry.Text; var caret = f.Entry.SelectionStart;
                f.Provider.Reply(1, "pending words"); await f.Session.Completion.WaitAsync(TimeSpan.FromSeconds(5));
                Check(f.Entry.Text == userText && f.Entry.SelectionStart == caret && f.Writes == 0 && f.Provider.Calls == 1 && f.Session.Review.HasText && f.ValidRetainedWave(),
                    "A " + conflict + " change preserves user state and pending text/audio, cancels the queue, and never follows another target.");
            }
            foreach (var discard in new[] { false, true })
            {
                await using var f = new PhraseFixture(dispatcher, Path.Combine(root, "cancel-" + discard));
                await f.StartAsync(); EmitPhrase(f.Capture, 1001); await WaitAsync(() => f.Provider.Calls == 1); EmitPhrase(f.Capture, 2001);
                f.Session.Cancel(discard); await f.Session.Completion.WaitAsync(TimeSpan.FromSeconds(5)); f.Provider.Reply(1, "abandoned late words");
                Check(f.Writes == 0 && f.Provider.Calls == 1 && f.Provider.Disposals == 1 && !f.Capture.Capturing &&
                    (discard ? f.Session.RetainedAudioPath is null && !f.Session.Review.HasText : f.ValidRetainedWave()),
                    "Cancellation never publishes late output; explicit Discard alone deletes pending captured speech (discard=" + discard + ").");
            }
            await using (var f = new PhraseFixture(dispatcher, Path.Combine(root, "uncertain")))
            {
                f.UncertainWrite = true; await f.StartAsync(); EmitPhrase(f.Capture, 1001); await WaitAsync(() => f.Provider.Calls == 1);
                EmitPhrase(f.Capture, 2001); f.Provider.Reply(1, "possibly applied"); await f.Session.Completion.WaitAsync(TimeSpan.FromSeconds(5));
                Check(f.Writes == 1 && f.Provider.Calls == 1 && f.Session.Review.InsertionUnconfirmed && f.ValidRetainedWave(),
                    "An uncertain Main write stops the queue, retains inspection-only words/audio and is never retried.");
            }
            await using (var f = new PhraseFixture(dispatcher, Path.Combine(root, "prepare")))
            {
                f.Provider.PrepareGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
                await f.StartAsync(waitCapture: false); await WaitAsync(() => f.Provider.PrepareModes.Count != 0);
                f.Session.FinishCurrent(); f.Provider.PrepareGate.SetResult(); await f.Session.Completion.WaitAsync(TimeSpan.FromSeconds(5));
                Check(f.Capture.Calls == 0 && f.Provider.Disposals == 1, "Stop during offline model preparation never opens capture.");
            }
            await using (var f = new PhraseFixture(dispatcher, Path.Combine(root, "total-cap")))
            {
                await f.StartAsync(); EmitPhrase(f.Capture, 1001); await WaitAsync(() => f.Provider.Calls == 1); f.Provider.Reply(1, "completed");
                await WaitAsync(() => f.Entry.Text == "Keep. completed Tail");
                f.Capture.CompleteAtTotalLimit(); await f.Session.Completion.WaitAsync(TimeSpan.FromSeconds(5));
                Check(f.Session.ContinuousAutomatic && f.Capture.Calls == 1 && f.Provider.Calls == 1 && f.Provider.Disposals == 1 &&
                    !f.Capture.Capturing && f.Session.LastMessage.Contains("two-minute recording limit") && f.Session.LastMessage.Contains("Start explicitly") && f.ValidRetainedWave(),
                    "The simulated inherited 120s cap drains confirmed speech and retains explicit recovery; it never reopens or replays capture.");
            }
            await using (var f = new PhraseFixture(dispatcher, Path.Combine(root, "visible-recovery")))
            {
                await f.Host.ExecuteServiceCommandAsync(Cerebral.Core.CompleteSettingsContract.PluginSaveOperation,
                    new Cerebral.Core.PluginSettingsTransferSaveRequest(new(AiDictationSettingsPolicy.Document(
                        new() { AutoTranscribeOnSilence = true, SilenceWaitSeconds = .5 }), 0, OwnerValidated: true)));
                var capture = new PhraseCapture(); var provider = new PhraseProvider();
                using var plugin = new AiDictationPlugin(capture, _ => provider);
                plugin.Initialize(f.Host); await dispatcher.InvokeAsync(() => { }, DispatcherPriority.Background);
                var view = (DictationMicrophoneView)plugin.MountContributions.OfType<DesktopMountView>().Single().CreateView();
                var toggle = typeof(AiDictationPlugin).GetMethod("ToggleStatusMicrophoneAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
                await (Task)toggle.Invoke(plugin, null)!; await capture.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
                EmitPhrase(capture, 1001); await WaitAsync(() => provider.Calls == 1); provider.Reply(1, "completed");
                await WaitAsync(() => f.Entry.Text == "Keep. completed Tail"); capture.CompleteAtTotalLimit();
                await WaitAsync(() => view.Snapshot.Error && view.Snapshot.HasRecording);
                var refresh = typeof(AiDictationPlugin).GetMethod("RefreshTargetViews", BindingFlags.Instance | BindingFlags.NonPublic)!;
                refresh.Invoke(plugin, null); refresh.Invoke(plugin, null);
                Check(view.Snapshot.Detail.Contains("two-minute recording limit") && view.Snapshot.Detail.Contains("Discard") &&
                    !view.Snapshot.CanToggleMicrophone && capture.Calls == 1 && provider.Calls == 1,
                    "The actual plugin controller keeps the cap/recovery warning visible after idle refresh and blocks implicit replacement.");
            }
            await using (var f = new PhraseFixture(dispatcher, Path.Combine(root, "native-admission"), live: true))
            {
                await f.StartAsync(waitCapture: false);
                Check(f.Capture.Calls == 0 && f.Provider.StreamingInspections == 1 && f.Session.LastState == DesktopTextEntryActivityState.Error,
                    "Explicit native live-word mode still refuses its missing asset before capture, independently of phrase admission.");
            }
            await using (var f = new PhraseFixture(dispatcher, Path.Combine(root, "manual"), live: true))
            {
                f.Session.BeginOneOff(f.Entry); await f.Session.ResumeAsync(f.Entry, default); await f.Capture.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
                for (var i = 0; i < 3; i++) f.Capture.Emit(PhrasePcm(1001));
                f.Session.FinishCurrent(); await WaitAsync(() => f.Provider.Calls == 1);
                f.Provider.Reply(1, "Manual words."); await f.Session.Completion.WaitAsync(TimeSpan.FromSeconds(5));
                Check(f.Capture.Options?.AutoTranscribeOnSilence == false && f.Provider.StreamingInspections == 0 &&
                    f.Session.Review.Text == "Manual words." && f.Entry.Text == "Keep. Tail" && f.Session.RetainedAudioPath is not null,
                    "One-off Record/Stop retains Play audio and reviewed words without requiring the streaming asset or auto-inserting.");
                Check((await f.Session.AcceptReviewAsync(default)).State == DesktopOperationState.Completed && f.Entry.Text == "Keep. Manual words. Tail" && f.Host.Submissions == 0,
                    "Explicit one-off Insert applies reviewed words once without submitting a message.");
            }
            using var widget = new DictationMicrophoneView(true, () => Task.CompletedTask, () => Task.CompletedTask, open: () => { });
            Check(LogicalButtons(widget).Any(button => AutomationProperties.GetAutomationId(button) == "AiDictation.OpenControls"),
                "Docked automatic controls retain the reachable floating one-off entry.");
        }
        finally
        {
            var resolved = Path.GetFullPath(root);
            if (!resolved.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase) ||
                !Path.GetFileName(resolved).StartsWith("cerebral-phrase-fake-", StringComparison.Ordinal)) throw new IOException("Unsafe phrase fixture cleanup.");
            if (Directory.Exists(resolved)) Directory.Delete(resolved, true);
        }
        return checks;
    }

    private static byte[] PhrasePcm(short value)
    {
        var pcm = new byte[3200]; for (var i = 0; i < pcm.Length; i += 2) BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(i), value); return pcm;
    }
    private static void EmitPhrase(PhraseCapture capture, short value)
    { capture.Emit(PhrasePcm(value)); for (var i = 0; i < 5; i++) capture.Emit(PhrasePcm(0)); }

    private sealed class PhraseFixture : IAsyncDisposable
    {
        internal readonly string Directory;
        internal readonly NativeEntry Entry;
        internal readonly TestHost Host;
        internal readonly PhraseCapture Capture = new();
        internal readonly PhraseProvider Provider = new();
        internal readonly AiDictationSession Session;
        internal TaskCompletionSource? WriteGate;
        internal readonly TaskCompletionSource WriteEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool Current = true, UncertainWrite;
        internal int Writes;
        private readonly Cerebral.Orchestrator.ComposerDraftPresentation effect;
        internal PhraseFixture(Dispatcher dispatcher, string directory, bool live = false,
            Func<AiDictationSettings, string, CancellationToken, Task<string>>? correct = null)
        {
            Directory = directory; Entry = new(dispatcher); Entry.Box.Text = "Keep. Tail"; Entry.Box.Select(5, 0);
            long revision = 1; Entry.Box.TextChanged += (_, _) => revision++;
            Host = new TestHost { DefaultTextEntry = Entry, SupportsDraftPresentation = true, Surface = DesktopPluginSurface.Orchestrator,
                DataDirectory = Path.Combine(directory, "settings") };
            Host.DraftReader = () => new(Host.Binding, revision, Entry.Text);
            effect = new(Entry.Box);
            Host.DraftWriter = async (update, token) =>
            {
                Writes++; WriteEntered.TrySetResult(); if (WriteGate is not null) await WriteGate.Task.WaitAsync(token);
                if (UncertainWrite) return new(DesktopOperationState.Uncertain, "Fixture lost insertion receipt.");
                return update.Binding == Host.Binding && update.ExpectedRevision == revision && update.Presentation is { } presentation
                    ? effect.Apply(update.Text, presentation) : new(DesktopOperationState.Rejected, "Fixture owner changed.");
            };
            Session = new(Host, directory, () => new() { AutoTranscribeOnSilence = true, SilenceWaitSeconds = .5,
                LiveWordsWhileSpeaking = live, NormalizeQuietAudio = false }, _ => Provider, Capture,
                action => dispatcher.InvokeAsync(action).Task.Unwrap(), (_, _) => { }, _ => Current, correct);
        }
        internal async Task StartAsync(bool waitCapture = true)
        {
            Session.BeginDestinationRun(Entry); await Session.ToggleAsync(Entry, default);
            if (waitCapture) await Capture.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        internal bool ValidRetainedWave()
        {
            if (Session.RetainedAudioPath is not { } path || !File.Exists(path)) return false;
            var bytes = File.ReadAllBytes(path);
            return bytes.Length > 44 && bytes.AsSpan(0, 4).SequenceEqual("RIFF"u8) && BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(40)) == bytes.Length - 44;
        }
        public async ValueTask DisposeAsync()
        {
            Session.Cancel(discardReview: true); await Session.Completion.WaitAsync(TimeSpan.FromSeconds(5));
            Session.ClearRetainedAudio(); Session.Dispose(); effect.Dispose(); Entry.Dispose();
        }
    }

    private sealed class PhraseCapture : IAiDictationIncrementalCapture
    {
        internal readonly TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int Calls;
        internal bool Capturing;
        internal AiDictationCaptureOptions? Options;
        private readonly TaskCompletionSource finishCapture = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private Action<byte[]>? push;
        private double? durationAtLimit;
        internal void Emit(byte[] pcm) { if (Capturing) push!(pcm); }
        internal void CompleteAtTotalLimit() { durationAtLimit = 120; finishCapture.SetResult(); }
        public Task<AiDictationCaptureResult> CaptureAsync(string path, AiDictationCaptureOptions options, CancellationToken finish, CancellationToken cancel, Action<double> level) =>
            CaptureIncrementalAsync(path, options, finish, cancel, level, _ => { });
        public async Task<AiDictationCaptureResult> CaptureIncrementalAsync(string path, AiDictationCaptureOptions options, CancellationToken finish,
            CancellationToken cancel, Action<double> level, Action<byte[]> pcm)
        {
            using var data = new MemoryStream(); Calls++; Capturing = true; Options = options;
            push = chunk => { data.Write(chunk); try { pcm(chunk); } catch (Exception error) { finishCapture.TrySetException(error); } };
            Started.TrySetResult();
            using var stopped = CancellationTokenSource.CreateLinkedTokenSource(finish, cancel);
            try
            {
                try { await finishCapture.Task.WaitAsync(stopped.Token); } catch (OperationCanceledException) when (!cancel.IsCancellationRequested) { }
                cancel.ThrowIfCancellationRequested(); return new(durationAtLimit ?? data.Length / 32000d, new(false, .3, .3, 0));
            }
            finally { Capturing = false; DictationPcmWave.Write(path, data.ToArray()); }
        }
    }
    private sealed class PhraseProvider : IResidentDictationProvider
    {
        internal int Calls, Loads, Disposals, StreamingInspections, MaximumInFlight;
        internal readonly List<bool> PrepareModes = [];
        internal readonly List<byte[]> Audio = [];
        internal TaskCompletionSource? PrepareGate;
        internal TaskCompletionSource? DisposalGate, ShutdownSignal;
        internal bool FailDisposal;
        public Task ShutdownCompletion => ShutdownSignal?.Task ?? Task.CompletedTask;
        private readonly List<TaskCompletionSource<string>> replies = [];
        private int inFlight;
        public SpeechProviderDescriptor Descriptor => new("phrase-fake", "1", "deterministic offline fixture", true);
        public SpeechProviderAvailability Inspect(SpeechModelBinding _) => new(true, null);
        public SpeechProviderAvailability InspectStreaming() { StreamingInspections++; return new(false, "Fixture optional streaming asset is absent."); }
        public async Task PrepareAsync(bool streaming, CancellationToken token)
        { PrepareModes.Add(streaming); if (PrepareGate is not null) await PrepareGate.Task.WaitAsync(token); token.ThrowIfCancellationRequested(); Loads = 1; }
        public Task RecognizeStreamAsync(ChannelReader<byte[]> pcm, Func<DictationRecognition, Task> result, CancellationToken token) =>
            throw new InvalidOperationException("Offline phrase mode must not call native streaming.");
        public async Task<SpeechTranscript> TranscribeAsync(SpeechAudioInput audio, SpeechModelBinding model, CancellationToken token)
        {
            await PrepareAsync(false, token); var bytes = await File.ReadAllBytesAsync(audio.WavPath, token);
            var reply = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (replies) { Audio.Add(bytes[44..]); replies.Add(reply); Calls++; }
            MaximumInFlight = Math.Max(MaximumInFlight, ++inFlight);
            try { return new(await reply.Task.WaitAsync(token), (bytes.Length - 44) / 32000d, 1, Descriptor.Id, model.ModelId); }
            finally { inFlight--; }
        }
        internal void Reply(int call, string text) { lock (replies) replies[call - 1].TrySetResult(text); }
        public async ValueTask DisposeAsync()
        {
            Disposals++;
            if (DisposalGate is not null) await DisposalGate.Task;
            if (FailDisposal) throw new IOException("Fixture helper exit is unconfirmed.");
        }
    }
}
