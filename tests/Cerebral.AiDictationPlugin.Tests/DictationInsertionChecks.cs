using System.Windows.Controls;
using System.Windows.Threading;
using Cerebral.AiDictationPlugin;
using Cerebral.Desktop.Plugins;
using Cerebral.Dictation;

internal static partial class Program
{
    private static async Task<int> RunInsertionChecksAsync(Dispatcher dispatcher, string root, TestHost host)
    {
        var checks = 0;
        void Check(bool value, string label) { if (!value) throw new InvalidOperationException(label); checks++; }
        foreach (var sample in new[]
        {
            ("Keep this.", 0, 10, "More words.", "Keep this. More words.", 22),
            ("red blue green", 4, 4, "violet", "red blue violet green", 15),
            ("AB", 1, 0, "first", "A first B", 8),
            ("hello world", 5, 0, ",", "hello, world", 6)
        })
        {
            using var entry = new NativeEntry(dispatcher);
            entry.Box.Text = sample.Item1; entry.Box.Select(sample.Item2, sample.Item3);
            var span = DictatedTextInsertion.Capture(entry);
            var changed = 0; entry.Box.TextChanged += (_, _) => changed++;
            var result = await entry.InsertAsync(span.Prepare(sample.Item4));
            Check(result.State == DesktopOperationState.Completed && entry.Box.Text == sample.Item5,
                "actual WPF insertion preserves selected original text and separates dictated words");
            Check(entry.Box.SelectionLength == 0 && entry.Box.CaretIndex == sample.Item6 && changed == 1,
                "actual WPF insertion emits one text event and leaves the caret after the dictated span");
        }

        using var resumed = new NativeEntry(dispatcher);
        resumed.Box.Text = "Keep this."; resumed.Box.SelectAll();
        async Task<AiDictationSession> Start(NativeEntry entry, string transcript, Action? whileRecognizing = null)
        {
            var capture = new FakeCapture(); var provider = new FakeProvider();
            var session = new AiDictationSession(host, root, () => new(), _ => provider, capture,
                action => dispatcher.InvokeAsync(action).Task.Unwrap(), (_, _) => { });
            await session.ToggleAsync(entry, CancellationToken.None); var work = session.Completion;
            await capture.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));
            await session.ToggleAsync(entry, CancellationToken.None);
            await provider.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));
            whileRecognizing?.Invoke(); provider.Result.SetResult(Transcript(transcript));
            await work.WaitAsync(TimeSpan.FromSeconds(3));
            return session;
        }
        using (var first = await Start(resumed, "More words."))
            Check(first.LastInsertionSucceeded && resumed.Text == "Keep this. More words.", "first selected-all segment preserves the original WPF draft");
        resumed.Box.SelectAll();
        using (var second = await Start(resumed, "Another segment."))
            Check(second.LastInsertionSucceeded && resumed.Text == "Keep this. More words. Another segment.", "pause/resume with selected-all preserves every earlier segment");

        var before = resumed.Text;
        using (var changed = await Start(resumed, "Must not overwrite.", () => resumed.Box.Text = "Manually edited while recognizing"))
            Check(!changed.LastInsertionSucceeded && resumed.Text == "Manually edited while recognizing",
                "late recognition preserves manual edits and rejects a stale captured text span");
        resumed.Box.Text = before; resumed.Box.Select(before.Length, 0);
        using (var moved = await Start(resumed, "Must not move.", () => resumed.Box.Select(0, 0)))
            Check(!moved.LastInsertionSucceeded && resumed.Text == before && resumed.Box.CaretIndex == 0,
                "late recognition preserves a moved caret and rejects a stale captured selection");
        return checks;
    }

    private sealed class NativeEntry(Dispatcher dispatcher) : IDesktopTextEntry, IDesktopTextEntryLifetime, IDisposable
    {
        internal TextBox Box { get; } = new();
        private readonly CancellationTokenSource available = new();
        public string Id => "native-regression";
        public string Purpose => "Owned offscreen WPF text regression";
        public string Text => Box.Text;
        public int SelectionStart => Box.SelectionStart;
        public int SelectionLength => Box.SelectionLength;
        public CancellationToken AvailabilityToken => available.Token;
        public Task<DesktopOperationResult> InsertAsync(string text, CancellationToken cancellationToken = default)
        {
            if (!dispatcher.CheckAccess()) throw new InvalidOperationException("Insertion left the WPF owner.");
            cancellationToken.ThrowIfCancellationRequested(); available.Token.ThrowIfCancellationRequested();
            var start = Box.SelectionStart;
            Box.SelectedText = text; Box.Select(start + text.Length, 0);
            return Task.FromResult(new DesktopOperationResult(DesktopOperationState.Completed));
        }
        public void Dispose() { available.Cancel(); available.Dispose(); }
    }
}
