using System.IO;
using System.Reflection;
using System.Windows.Threading;
using Cerebral.AiDictationPlugin;
using Cerebral.Desktop.Plugins;
using Cerebral.Dictation;

internal static partial class Program
{
    private static async Task<int> RunDestinationChecksAsync(Dispatcher dispatcher)
    {
        var checks = 0;
        void Check(bool value, string label) { if (!value) throw new InvalidOperationException(label); checks++; }
        object? Invoke(AiDictationPlugin plugin, string method, params object[] args) =>
            typeof(AiDictationPlugin).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(plugin, args);
        Task<IDesktopTextEntry?> Resolve(AiDictationPlugin plugin) => (Task<IDesktopTextEntry?>)Invoke(plugin, "ResolveStartTargetAsync")!;
        DictationReview Review(AiDictationPlugin plugin) => ((AiDictationSession)typeof(AiDictationPlugin)
            .GetField("session", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(plugin)!).Review;
        Check(new AiDictationSettings().DockControls, "Fresh settings default to docked controls.");
        Check(AiDictationSettingsPolicy.PrepareLegacy("{\"SchemaVersion\":1}"u8.ToArray()).DockControls,
            "An absent legacy dock preference uses docking without a migration write.");
        Check(!AiDictationSettingsPolicy.Decode(AiDictationSettingsPolicy.Encode(new() { DockControls = false })).DockControls,
            "An explicit saved floating choice remains floating.");

        var fixture = Path.Combine(Path.GetTempPath(), "cerebral-dictation-destination-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var composer = new NativeEntry(dispatcher);
            composer.Box.Text = "Cerebral prompt"; composer.Box.Select(8, 0);
            var host = new TestHost { DataDirectory = fixture, Surface = DesktopPluginSurface.Orchestrator, DefaultTextEntry = composer };
            var observerStarts = 0;
            Action<IChosenWindowsTextTarget>? remember = null;
            var observation = new DestinationObservation();
            var capture = new FakeCapture();
            using var plugin = new AiDictationPlugin(capture, _ => throw new InvalidOperationException("No provider may run in destination unit checks."),
                observeWindowsFocus: callback => { observerStarts++; remember = callback; return observation; });
            plugin.Initialize(host);
            await dispatcher.InvokeAsync(() => { }, DispatcherPriority.Background);
            Check(observerStarts == 0 && !capture.Started.Task.IsCompleted, "Permission-off startup neither registers a Windows observer nor starts capture.");
            Check(ReferenceEquals(await Resolve(plugin), composer) && composer.Box.SelectionStart == 8,
                "No chosen/history target resolves the genuine host composer without fabricating focus or cursor.");
            host.DefaultTextEntry = null;
            Check(await Resolve(plugin) is null, "Missing optional composer capability/entry stays unavailable instead of inventing one.");
            host.DefaultTextEntry = composer;
            using var answer = new NativeEntry(dispatcher);
            host.Focus(answer); host.Focus(null);
            Check(ReferenceEquals(await Resolve(plugin), answer), "Opening plugin review/settings retains the actual last eligible Cerebral field.");

            await (Task)Invoke(plugin, "SaveWindowOptionAsync", (Func<AiDictationSettings, AiDictationSettings>)(value => value with { AllowExternalTarget = true }))!;
            Check(observerStarts == 1 && remember is not null && !capture.Started.Task.IsCompleted,
                "Only acknowledged opt-in registers destination memory; it does not arm capture.");
            var native = new DestinationNativeTarget(new("windows-fixture", "Fixture Windows text box", "Initial", 2, 0));
            remember!(native);
            await dispatcher.InvokeAsync(() => { }, DispatcherPriority.Background);
            Check(native.Reads == 0 && native.Writes == 0, "A focus observation stores identity only, reading no target text or cursor.");
            native.Snapshot = native.Snapshot with { Text = "Changed before Start", Start = 7 };
            var selected = (WindowsDictationTarget)(await Resolve(plugin))!;
            Check(selected.Text == "Changed before Start" && selected.SelectionStart == 7 && native.Reads == 1,
                "Start captures fresh text/cursor from exactly the remembered Windows control.");
            Invoke(plugin, "LockTarget", selected);
            var review = Review(plugin);
            review.Append(selected, DictatedTextInsertion.Capture(selected), "Retained words");
            var later = new DestinationNativeTarget(new("later-fixture", "Other Windows text box", "Other", 0, 0));
            remember(later); host.Focus(composer); host.Focus(null);
            await dispatcher.InvokeAsync(() => { }, DispatcherPriority.Background);
            Check(ReferenceEquals(await Resolve(plugin), selected) && later.Reads == 0 && native.Writes == 0,
                "Later Windows/Cerebral focus cannot redirect a retained review or read another control.");
            native.Snapshot = native.Snapshot with { Text = "Human changed destination", Start = 3 };
            var refused = await review.AcceptAsync(CancellationToken.None);
            Check(refused.State == DesktopOperationState.Rejected && review.Text == "Retained words" && native.Writes == 0,
                "A conflicting destination refuses Insert and retains the review without writing.");
            review.Clear();
            Invoke(plugin, "UseRememberedDestination");
            remember(later);
            await dispatcher.InvokeAsync(() => { }, DispatcherPriority.Background);
            later.ReadFailure = true;
            var failed = false;
            try { await Resolve(plugin); } catch (InvalidOperationException) { failed = true; }
            Check(failed && later.Writes == 0, "A stale remembered control refuses Start rather than falling back into a different app.");
            var oldCallback = remember;
            await (Task)Invoke(plugin, "SaveWindowOptionAsync", (Func<AiDictationSettings, AiDictationSettings>)(value => value with { AllowExternalTarget = false }))!;
            oldCallback(native);
            await dispatcher.InvokeAsync(() => { }, DispatcherPriority.Background);
            Check(observation.Disposals == 1 && ReferenceEquals(await Resolve(plugin), composer),
                "Revoking opt-in disposes the observer, discards external memory and ignores late callbacks.");
            Check(!capture.Started.Task.IsCompleted && native.Writes == 0 && later.Writes == 0 && host.Submissions == 0,
                "Destination unit checks never start microphone/provider, modify an app, or submit a message.");

            var activeHost = new TestHost { DataDirectory = Path.Combine(fixture, "active"), Surface = DesktopPluginSurface.Orchestrator };
            var fakeCapture = new FakeCapture();
            Action<IChosenWindowsTextTarget>? activeRemember = null;
            using var activePlugin = new AiDictationPlugin(fakeCapture, _ => new SequenceProvider("One-off words."),
                observeWindowsFocus: callback => { activeRemember = callback; return new DestinationObservation(); });
            activePlugin.Initialize(activeHost);
            await (Task)Invoke(activePlugin, "SaveWindowOptionAsync", (Func<AiDictationSettings, AiDictationSettings>)(value => value with { AllowExternalTarget = true }))!;
            var first = new DestinationNativeTarget(new("active-first", "Capture fixture", "Keep", 4, 0));
            var second = new DestinationNativeTarget(new("active-second", "Later fixture", "Other", 0, 0));
            activeRemember!(first);
            await dispatcher.InvokeAsync(() => { }, DispatcherPriority.Background);
            await (Task)Invoke(activePlugin, "ToggleControlMicrophoneAsync")!;
            await fakeCapture.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var captureTarget = await Resolve(activePlugin);
            activeRemember(second);
            await dispatcher.InvokeAsync(() => { }, DispatcherPriority.Background);
            Check(ReferenceEquals(await Resolve(activePlugin), captureTarget) && second.Reads == 0,
                "A running fake capture keeps its Start destination despite later Windows focus.");
            Invoke(activePlugin, "FinishControlMicrophone");
            var activeSession = (AiDictationSession)typeof(AiDictationPlugin).GetField("session", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(activePlugin)!;
            await activeSession.Completion.WaitAsync(TimeSpan.FromSeconds(5));
            Check(activeSession.Review.Text == "One-off words." && ReferenceEquals(activeSession.Review.Target, captureTarget) && first.Writes == 0 && second.Writes == 0,
                "A focus change stops the fake capture and retains its phrase instead of writing into either target.");
            activeSession.Review.Clear();
            Invoke(activePlugin, "UseRememberedDestination");
            var blocked = new DestinationNativeTarget(new("pending-start", "Delayed fixture", "Keep", 4, 0))
                { Entered = new(TaskCreationOptions.RunContinuationsAsynchronously), Release = new(false) };
            activeRemember(blocked);
            await dispatcher.InvokeAsync(() => { }, DispatcherPriority.Background);
            var pendingStart = (Task)Invoke(activePlugin, "ToggleControlMicrophoneAsync")!;
            await blocked.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Invoke(activePlugin, "FinishControlMicrophone");
            blocked.Release.Set();
            await pendingStart.WaitAsync(TimeSpan.FromSeconds(5));
            Check(!fakeCapture.SecondStarted.Task.IsCompleted && blocked.Writes == 0,
                "Stop during a delayed Start snapshot prevents a late microphone restart.");
            blocked.Release.Dispose();
        }
        finally
        {
            var resolved = Path.GetFullPath(fixture);
            if (!resolved.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase) ||
                !Path.GetFileName(resolved).StartsWith("cerebral-dictation-destination-", StringComparison.Ordinal))
                throw new IOException("Unsafe destination fixture cleanup path.");
            if (Directory.Exists(resolved)) Directory.Delete(resolved, true);
        }
        return checks;
    }

    private sealed class DestinationObservation : IDisposable
    {
        internal int Disposals;
        public void Dispose() => Disposals++;
    }
    private sealed class DestinationNativeTarget(WindowsTextTargetSnapshot snapshot) : IChosenWindowsTextTarget
    {
        public string Identity => Snapshot.Identity;
        internal WindowsTextTargetSnapshot Snapshot = snapshot;
        internal int Reads, Writes;
        internal bool ReadFailure;
        internal TaskCompletionSource? Entered;
        internal ManualResetEventSlim? Release;
        public WindowsTextTargetSnapshot Read(CancellationToken token)
        { token.ThrowIfCancellationRequested(); Reads++; Entered?.TrySetResult(); Release?.Wait(TimeSpan.FromSeconds(5), token);
            if (ReadFailure) throw new InvalidOperationException("Fixture target retired."); return Snapshot; }
        public void ReplaceSelection(WindowsTextTargetSnapshot expected, string text, CancellationToken token)
        { token.ThrowIfCancellationRequested(); Writes++; throw new InvalidOperationException("Conflicting fixtures must never dispatch."); }
    }
}
