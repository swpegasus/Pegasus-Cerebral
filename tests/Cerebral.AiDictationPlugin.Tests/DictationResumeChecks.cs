using System.IO;
using System.Reflection;
using System.Windows.Threading;
using Cerebral.AiDictationPlugin;
using Cerebral.Desktop.Plugins;
using Cerebral.Speech;

internal static partial class Program
{
    private static async Task<int> RunResumeChecksAsync(Dispatcher dispatcher, string root, TestHost host)
    {
        var checks = 0;
        void Check(bool ok, string message) { if (!ok) throw new InvalidOperationException(message); checks++; }
        var capture = new DelayedCancelCapture(); var provider = new SequenceProvider("segment-1");
        using var entry = new NativeEntry(dispatcher); entry.Box.Text = "Keep"; entry.Box.CaretIndex = 4;
        using (var session = new AiDictationSession(host, root, () => new(), _ => provider, capture,
            action => dispatcher.InvokeAsync(action).Task.Unwrap(), (_, _) => { }))
        {
            await session.ToggleAsync(entry, default); await capture.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));
            session.Cancel(); await capture.Cancelling.Task.WaitAsync(TimeSpan.FromSeconds(3));
            var resume = session.ResumeAsync(entry, default);
            Check(!resume.IsCompleted, "Resume waits for the cancelled native segment to settle");
            capture.ReleaseCancellation.TrySetResult(); await resume.WaitAsync(TimeSpan.FromSeconds(3));
            await WaitAsync(() => capture.Calls == 2);
            Check(session.IsActive && entry.Text == "Keep", "Resume starts a fresh same-target segment without altering earlier text");
            var completion = session.Completion; await session.ToggleAsync(entry, default);
            await completion.WaitAsync(TimeSpan.FromSeconds(3));
            Check(session.LastInsertionSucceeded && entry.Text == "Keep segment-1", "Only the fresh resumed segment inserts into its captured native caret");
        }

        capture = new DelayedCancelCapture(); provider = new SequenceProvider("segment-1");
        using var first = new NativeEntry(dispatcher); using var second = new NativeEntry(dispatcher); using var newest = new NativeEntry(dispatcher);
        using (var session = new AiDictationSession(host, root, () => new(), _ => provider, capture,
            action => dispatcher.InvokeAsync(action).Task.Unwrap(), (_, _) => { }))
        {
            await session.ToggleAsync(first, default); await capture.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));
            var superseded = session.ToggleAsync(second, default); await capture.Cancelling.Task.WaitAsync(TimeSpan.FromSeconds(3));
            var latest = session.ToggleAsync(newest, default); capture.ReleaseCancellation.TrySetResult();
            try { await superseded; throw new InvalidOperationException("A superseded start was accepted."); } catch (OperationCanceledException) { }
            await latest.WaitAsync(TimeSpan.FromSeconds(3)); await WaitAsync(() => capture.Calls == 2);
            var completion = session.Completion; await session.ToggleAsync(newest, default); await completion.WaitAsync(TimeSpan.FromSeconds(3));
            Check(capture.Calls == 2 && first.Text == "" && second.Text == "" && newest.Text == "segment-1",
                "competing pending starts admit only the latest target and never overwrite session ownership");
        }

        capture = new DelayedCancelCapture(); provider = new SequenceProvider("segment-1");
        using (var plugin = new AiDictationPlugin(capture, _ => provider))
        {
            plugin.Initialize(host); var record = plugin.TextEntryActions.Single(action => action.Id == AiDictationSession.ActionId);
            await record.ExecuteAsync(first, default); await capture.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));
            var pending = record.ExecuteAsync(second, default); await capture.Cancelling.Task.WaitAsync(TimeSpan.FromSeconds(3));
            typeof(AiDictationPlugin).GetMethod("LockTarget", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(plugin, [newest]);
            capture.ReleaseCancellation.TrySetResult(); await pending.WaitAsync(TimeSpan.FromSeconds(3));
            Check(capture.Calls == 1 && ReferenceEquals(typeof(AiDictationPlugin).GetField("lockedTarget", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(plugin), newest),
                "a newer explicit lock invalidates an older pending Record before it can start");
        }
        capture = new DelayedCancelCapture(); provider = new SequenceProvider("segment-1");
        var automaticHost = new TestHost { DataDirectory = Path.Combine(root, "automatic-resume") };
        var focused = new FocusEntry("automatic-resume", dispatcher, automaticHost) { Value = "Keep", Caret = 4 };
        using (var plugin = new AiDictationPlugin(capture, _ => provider))
        {
            plugin.Initialize(automaticHost); automaticHost.Focus(focused);
            await plugin.TextEntryActions.Single(action => action.Id == "ai-dictate-auto").ExecuteAsync(focused, default);
            await capture.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));
            var type = typeof(AiDictationPlugin);
            type.GetField("automaticPaused", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(plugin, true);
            type.GetMethod("StopAutomatic", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(plugin, [true, true]);
            type.GetField("automaticPaused", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(plugin, false);
            type.GetMethod("StartAutomatic", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(plugin, [focused]);
            await capture.Cancelling.Task.WaitAsync(TimeSpan.FromSeconds(3)); capture.ReleaseCancellation.TrySetResult();
            await WaitAsync(() => capture.Calls == 2);
            Check(focused.Value == "Keep" && ReferenceEquals(type.GetField("automaticTarget", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(plugin), focused),
                "automatic Resume supersedes a cancelled same-target loop and starts a fresh segment");
            automaticHost.Focus(null);
        }
        capture = new DelayedCancelCapture(); provider = new SequenceProvider("unused");
        var focusEntry = new DelayedFocusEntry();
        using (var plugin = new AiDictationPlugin(capture, _ => provider))
        {
            plugin.Initialize(host); var type = typeof(AiDictationPlugin);
            type.GetMethod("LockTarget", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(plugin, [focusEntry]);
            var resume = (Task)type.GetMethod("ResumeLockedTargetAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(plugin, null)!;
            await focusEntry.Requested.Task.WaitAsync(TimeSpan.FromSeconds(3));
            type.GetField("automaticPaused", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(plugin, true);
            type.GetMethod("StopAutomatic", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(plugin, [true, true]);
            focusEntry.Reply.SetResult(new(DesktopOperationState.Completed));
            try { await resume; throw new InvalidOperationException("Stale Resume was accepted."); } catch (OperationCanceledException) { }
            Check((bool)type.GetField("automaticPaused", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(plugin)! && capture.Calls == 0 && focusEntry.Cancellation.IsCancellationRequested,
                "a late focus acknowledgement cannot undo a newer Pause or start recording");
        }
        return checks;
    }

    private sealed class DelayedFocusEntry : IDesktopTextEntry, IDesktopTextEntryFocus
    {
        internal TaskCompletionSource Requested { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<DesktopOperationResult> Reply { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal CancellationToken Cancellation;
        public string Id => "delayed-focus"; public string Purpose => "Delayed focus fixture"; public string Text => "Keep";
        public int SelectionStart => 4; public int SelectionLength => 0; public bool IsKeyboardFocusWithin => false;
        public Task<DesktopOperationResult> FocusAsync(CancellationToken cancellationToken = default) { Cancellation = cancellationToken; Requested.TrySetResult(); return Reply.Task; }
        public Task<DesktopOperationResult> InsertAsync(string text, CancellationToken cancellationToken = default) => throw new InvalidOperationException("Stale Resume cannot insert.");
    }

    private sealed class DelayedCancelCapture : IAiDictationCapture
    {
        private int calls;
        internal int Calls => Volatile.Read(ref calls);
        internal TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Cancelling { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource ReleaseCancellation { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<AiDictationCaptureResult> CaptureAsync(string path, AiDictationCaptureOptions options, CancellationToken finish, CancellationToken cancel, Action<double> level)
        {
            var number = Interlocked.Increment(ref calls); await File.WriteAllTextAsync(path, "fixture recording", cancel);
            if (number == 1)
            {
                Started.TrySetResult();
                try { await Task.Delay(Timeout.InfiniteTimeSpan, cancel); }
                catch (OperationCanceledException) { Cancelling.TrySetResult(); await ReleaseCancellation.Task; throw; }
            }
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(finish, cancel);
            try { await Task.Delay(Timeout.InfiniteTimeSpan, linked.Token); }
            catch (OperationCanceledException) when (!cancel.IsCancellationRequested) { }
            cancel.ThrowIfCancellationRequested(); return new(1, new(false, .3, .3, 0));
        }
    }
}
