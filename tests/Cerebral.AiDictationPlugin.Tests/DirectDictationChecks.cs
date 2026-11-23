using System.IO;
using System.Reflection;
using System.Windows.Threading;
using Cerebral.AiDictationPlugin;
using Cerebral.Desktop.Plugins;
using Cerebral.Dictation;

internal static partial class Program
{
    // Authored for the cohesive1.2.2 batch. No real microphone, UIA registration/app access or inference.
    private static async Task<int> RunDirectDictationChecksAsync(Dispatcher dispatcher)
    {
        var checks = 0;
        void Check(bool ok, string label) { if (!ok) throw new InvalidOperationException(label); checks++; }
        using (var target = new NativeEntry(dispatcher))
        {
            target.Box.Text = "Keep."; target.Box.Select(5, 0);
            long revision = 10;
            target.Box.TextChanged += (_, _) => revision++;
            var run = new DictationDestinationRun(target, DictatedTextInsertion.Capture(target), () => revision, revision);
            var pending = new DictationReview(); pending.Append(target, run.Expected, "First phrase.");
            Check((await run.InsertPhraseAsync(pending, CancellationToken.None)).State == DesktopOperationState.Completed &&
                target.Text == "Keep. First phrase." && !pending.HasText && revision == 11,
                "A recognized phrase goes directly into the destination and confirms the exact predicted cursor/revision.");
            pending.Append(target, run.Expected, "Next phrase.");
            Check((await run.InsertPhraseAsync(pending, CancellationToken.None)).State == DesktopOperationState.Completed &&
                target.Text == "Keep. First phrase. Next phrase." && target.SelectionStart == target.Text.Length && revision == 12,
                "The next phrase rebases only from a confirmed self-insertion, preserving boundary spacing.");
            var original = target.Text;
            target.Box.Text += " Edit"; target.Box.Text = original; target.Box.Select(original.Length, 0);
            pending.Append(target, run.Expected, "Retain this.");
            Check((await run.InsertPhraseAsync(pending, CancellationToken.None)).State == DesktopOperationState.Rejected &&
                target.Text == original && pending.Text == "Retain this.",
                "An edit-and-undo revision change refuses insertion even when text and caret look identical.");
        }
        var uncertain = new DirectEntry { ConfirmWrongText = true };
        var uncertainRun = new DictationDestinationRun(uncertain, DictatedTextInsertion.Capture(uncertain), () => null, null);
        var retained = new DictationReview(); retained.Append(uncertain, uncertainRun.Expected, "Maybe.");
        Check((await uncertainRun.InsertPhraseAsync(retained, CancellationToken.None)).State == DesktopOperationState.Uncertain &&
            retained.InsertionUnconfirmed && retained.HasText && uncertain.Writes == 1,
            "A Completed result without exact text/cursor readback is uncertain and retains the phrase.");
        await uncertainRun.InsertPhraseAsync(retained, CancellationToken.None);
        await retained.AcceptAsync(CancellationToken.None);
        Check(uncertain.Writes == 1 && retained.HasText, "Neither the run nor recovery can replay an unconfirmed write.");
        var focusTarget = new DirectEntry();
        var focusedRun = new DictationDestinationRun(focusTarget, DictatedTextInsertion.Capture(focusTarget), () => null, null);
        var focusPending = new DictationReview(); focusPending.Append(focusTarget, focusedRun.Expected, "Waiting words.");
        focusedRun.Interrupt("Focus changed.");
        Check((await focusedRun.InsertPhraseAsync(focusPending, CancellationToken.None)).State == DesktopOperationState.Rejected &&
            focusTarget.Writes == 0 && focusPending.HasText, "A focus interruption stops direct insertion and retains pending words.");

        var fixture = Path.Combine(Path.GetTempPath(), "cerebral-direct-dictation-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var main = new NativeEntry(dispatcher);
            main.Box.Text = "Prompt."; main.Box.Select(7, 0);
            long mainRevision = 4;
            main.Box.TextChanged += (_, _) => mainRevision++;
            var host = new TestHost { DataDirectory = fixture, DefaultTextEntry = main };
            host.DraftReader = () => new(host.Binding, mainRevision, main.Text);
            var capture = new FakeCapture(); var provider = new FakeProvider();
            using var session = new AiDictationSession(host, fixture, () => new() { AutoTranscribeOnSilence = false },
                _ => provider, capture, action => dispatcher.InvokeAsync(action).Task.Unwrap(), (_, _) => { });
            await session.ToggleAsync(main, CancellationToken.None);
            await capture.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            session.FinishCurrent(); session.FinishCurrent();
            await provider.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            provider.Result.SetResult(Transcript("Final phrase."));
            await session.Completion.WaitAsync(TimeSpan.FromSeconds(5));
            Check(main.Text == "Prompt. Final phrase." && !session.Review.HasText && session.LastInsertionSucceeded && provider.Calls == 1,
                "Repeated Stop flushes a fake record-until-Stop phrase exactly once without a separate Insert action.");
            Check((await session.PrepareSubmissionAsync(main, CancellationToken.None)).State == DesktopOperationState.Completed && host.Submissions == 0,
                "Completed dictation leaves ordinary human editing/Send available and never submits itself.");

            var interruptedCapture = new FakeCapture(); var interruptedProvider = new FakeProvider();
            using var interrupted = new AiDictationSession(host, fixture, () => new() { AutoTranscribeOnSilence = true },
                _ => interruptedProvider, interruptedCapture, action => dispatcher.InvokeAsync(action).Task.Unwrap(), (_, _) => { });
            await interrupted.ToggleAsync(main, CancellationToken.None);
            await interruptedCapture.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            interrupted.InterruptDestination("The user focused another editor.");
            await interruptedProvider.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            interruptedProvider.Result.SetResult(Transcript("Keep pending."));
            await interrupted.Completion.WaitAsync(TimeSpan.FromSeconds(5));
            Check(interrupted.Review.Text == "Keep pending." && main.Text == "Prompt. Final phrase." &&
                interrupted.LastState == DesktopTextEntryActivityState.Error && host.Submissions == 0,
                "A focus-stopped fake capture still transcribes into recovery without writing or submitting.");

            using var statusMain = new NativeEntry(dispatcher); statusMain.Box.Text = "Main."; statusMain.Box.Select(5, 0);
            long statusRevision = 2; statusMain.Box.TextChanged += (_, _) => statusRevision++;
            var statusHost = new TestHost { DataDirectory = Path.Combine(fixture, "status"), Surface = DesktopPluginSurface.Orchestrator, DefaultTextEntry = statusMain };
            statusHost.DraftReader = () => new(statusHost.Binding, statusRevision, statusMain.Text);
            var statusCapture = new FakeCapture(); var statusProvider = new FakeProvider();
            Action<IChosenWindowsTextTarget>? remember = null;
            using var plugin = new AiDictationPlugin(statusCapture, _ => statusProvider,
                observeWindowsFocus: callback => { remember = callback; return new DestinationObservation(); });
            plugin.Initialize(statusHost);
            object? Invoke(string name, params object[] args) => typeof(AiDictationPlugin).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(plugin, args);
            await (Task)Invoke("SaveWindowOptionAsync", (Func<AiDictationSettings, AiDictationSettings>)(value => value with { AllowExternalTarget = true }))!;
            var windows = new DestinationNativeTarget(new("remembered-fixture", "Other fake app", "Other", 0, 0));
            remember!(windows); await dispatcher.InvokeAsync(() => { }, DispatcherPriority.Background);
            await (Task)Invoke("ToggleStatusMicrophoneAsync")!;
            await statusCapture.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Invoke("FinishControlMicrophone");
            await statusProvider.Started.Task.WaitAsync(TimeSpan.FromSeconds(5)); statusProvider.Result.SetResult(Transcript("Direct phrase."));
            var statusSession = (AiDictationSession)typeof(AiDictationPlugin).GetField("session", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(plugin)!;
            await statusSession.Completion.WaitAsync(TimeSpan.FromSeconds(5));
            Check(statusMain.Text == "Main. Direct phrase." && windows.Reads == 0 && windows.Writes == 0 && statusHost.Submissions == 0,
                "Status Start always dictates directly to the real Main prompt, leaving floating Windows history untouched.");
        }
        finally
        {
            var resolved = Path.GetFullPath(fixture);
            if (!resolved.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase) ||
                !Path.GetFileName(resolved).StartsWith("cerebral-direct-dictation-", StringComparison.Ordinal)) throw new IOException("Unsafe direct dictation fixture cleanup path.");
            if (Directory.Exists(resolved)) Directory.Delete(resolved, true);
        }
        return checks;
    }

    private sealed class DirectEntry : IDesktopTextEntry
    {
        internal bool ConfirmWrongText;
        internal int Writes;
        public string Id => "direct-fixture";
        public string Purpose => "Fake direct destination";
        public string Text { get; private set; } = "";
        public int SelectionStart { get; private set; }
        public int SelectionLength => 0;
        public Task<DesktopOperationResult> InsertAsync(string text, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested(); Writes++;
            Text = ConfirmWrongText ? "Unexpected app change" : Text.Insert(SelectionStart, text);
            SelectionStart = Text.Length; return Task.FromResult(new DesktopOperationResult(DesktopOperationState.Completed));
        }
    }
}
