using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;
using Cerebral.AiDictationPlugin;
using Cerebral.Core;
using Cerebral.Desktop.Plugins;
using Cerebral.Speech;

internal static partial class Program
{
    // Source-only until the coordinator admits the coherent live-backend batch.
    private static async Task<int> RunManualDictationChecksAsync(Dispatcher dispatcher)
    {
        var checks = 0;
        void Check(bool ok, string label) { if (!ok) throw new InvalidOperationException(label); checks++; }
        object? Invoke(AiDictationPlugin plugin, string name, params object[] args) =>
            typeof(AiDictationPlugin).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(plugin, args);
        AiDictationSession Session(AiDictationPlugin plugin) => (AiDictationSession)typeof(AiDictationPlugin)
            .GetField("session", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(plugin)!;
        var fixture = Path.Combine(Path.GetTempPath(), "cerebral-dictation-manual-" + Guid.NewGuid().ToString("N"));
        try
        {
            var tools = Path.Combine(fixture, "tools"); var models = Path.Combine(fixture, "models");
            var runtime = Path.Combine(tools, "AI-Dictation", "NeMo-Speech", "bin", "nemo-speech.exe");
            var model = Path.Combine(models, "AI-Dictation", "parakeet-tdt-0.6b-v3.q8_0.gguf");
            Directory.CreateDirectory(Path.GetDirectoryName(runtime)!); Directory.CreateDirectory(Path.GetDirectoryName(model)!);
            // Presence-only fixtures are never executed or loaded as a model.
            File.WriteAllText(runtime, "fixture executable placeholder"); File.WriteAllText(model, "fixture model placeholder");
            var productPaths = new DesktopProductPaths(fixture, fixture, fixture, Path.Combine(fixture, "projects"), models, tools, Path.Combine(fixture, "shared"));
            var store = new AiDictationSettingsStore(Path.Combine(fixture, "settings"), productPaths);
            var saved = new AiDictationSettings { AutoTranscribeOnSilence = true };
            var effective = store.ResolveRuntimePaths(saved);
            Check(effective.RuntimePath == runtime && effective.ModelPath == model && saved.RuntimePath.Length == 0 && saved.ModelPath.Length == 0,
                "Empty bundled choices resolve effectively from actual host roots without modifying acknowledged values.");
            var explicitPaths = saved with { RuntimePath = Path.Combine(fixture, "missing-custom.exe"), ModelPath = Path.Combine(fixture, "missing-custom.gguf") };
            Check(store.ResolveRuntimePaths(explicitPaths) == explicitPaths, "Invalid explicit choices are preserved instead of silently replaced.");
            Check(store.ResolveRuntimePaths(saved with { ModelId = "custom/model" }).ModelPath.Length == 0,
                "An empty custom model choice does not resolve to bundled weights.");
            Check(new AiDictationSettingsStore(Path.Combine(fixture, "unbound"), new("", "", "", "", "", "", "")).ResolveRuntimePaths(saved) == saved,
                "Missing absolute host roots never become guessed relative workspace paths.");

            using var target = new NativeEntry(dispatcher); target.Box.Text = "Existing."; target.Box.Select(9, 0);
            long revision = 1; target.Box.TextChanged += (_, _) => revision++;
            TestHost Host(string suffix) => new() { Surface = DesktopPluginSurface.Orchestrator, DataDirectory = Path.Combine(fixture, suffix),
                ProductPaths = productPaths, DefaultTextEntry = target, DraftReader = () => new(new("fixture", null), revision, target.Text) };
            async Task SaveAuto(TestHost host)
            {
                host.DraftReader = () => new(host.Binding, revision, target.Text);
                await host.ExecuteServiceCommandAsync(CompleteSettingsContract.PluginSaveOperation,
                    new PluginSettingsTransferSaveRequest(new(AiDictationSettingsPolicy.Document(saved), 0, OwnerValidated: true)));
            }
            var unavailableHost = Host("unavailable"); await SaveAuto(unavailableHost);
            var fakeCapture = new FakeCapture(); var providerCalls = 0; var fakePlay = new ManualFakePlayback();
            string? ownedClip;
            using (var plugin = new AiDictationPlugin(fakeCapture, _ => { providerCalls++; throw new InvalidOperationException("Fixture transcription unavailable."); }, playbackFactory: () => fakePlay))
            {
                plugin.Initialize(unavailableHost); await dispatcher.InvokeAsync(() => { }, DispatcherPriority.Background);
                var beforeRevision = unavailableHost.Settings.Revision;
                Check(plugin.ReadRuntimePreferences().RuntimePath == runtime && unavailableHost.Settings.Active!.Values.GetProperty("runtimePath").GetString() == "" &&
                    unavailableHost.Settings.Revision == beforeRevision && !fakeCapture.Started.Task.IsCompleted,
                    "Effective path resolution does not save preferences or start capture.");
                await (Task)Invoke(plugin, "ToggleOneOffMicrophoneAsync")!;
                await fakeCapture.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Check(providerCalls == 0 && fakeCapture.LastOptions?.AutoTranscribeOnSilence == false,
                    "One-off Record ignores saved pause mode without rewriting it and does not require an AI provider first.");
                Invoke(plugin, "FinishControlMicrophone"); await Session(plugin).Completion.WaitAsync(TimeSpan.FromSeconds(5));
                ownedClip = Session(plugin).RetainedAudioPath;
                Check(providerCalls == 1 && ownedClip is not null && File.Exists(ownedClip) && target.Text == "Existing.",
                    "A transcription failure retains the single owned recording for playback and does not insert.");
                await (Task)Invoke(plugin, "PlayRecordingAsync")!;
                Check(fakePlay.Calls == 1 && fakePlay.Path == ownedClip && unavailableHost.Submissions == 0,
                    "Explicit Play routes only the retained owned clip through the fake output adapter, never capture or submit.");
            }
            Check(ownedClip is not null && !File.Exists(ownedClip), "Disposal removes the retained one-off recording.");

            var goodHost = Host("success"); await SaveAuto(goodHost);
            var goodCapture = new FakeCapture();
            using (var plugin = new AiDictationPlugin(goodCapture, _ => new SequenceProvider("One-off words."), playbackFactory: () => fakePlay))
            {
                plugin.Initialize(goodHost); await dispatcher.InvokeAsync(() => { }, DispatcherPriority.Background);
                await (Task)Invoke(plugin, "ToggleOneOffMicrophoneAsync")!; await goodCapture.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Invoke(plugin, "FinishControlMicrophone"); await Session(plugin).Completion.WaitAsync(TimeSpan.FromSeconds(5));
                Check(Session(plugin).Review.Text == "One-off words." && target.Text == "Existing.",
                    "One-off Stop transcribes for review and preserves the destination until explicit Insert.");
                await (Task)Invoke(plugin, "AcceptCurrentReviewAsync")!;
                Check(target.Text == "Existing. One-off words." && !Session(plugin).Review.HasText && goodHost.Submissions == 0,
                    "One-off Insert applies the reviewed words once and never sends a message.");
            }

            var failedHost = Host("sticky"); await SaveAuto(failedHost);
            var noCapture = new FakeCapture();
            using (var plugin = new AiDictationPlugin(noCapture, _ => throw new InvalidOperationException("Fixture start failure remains visible.")))
            {
                plugin.Initialize(failedHost); await dispatcher.InvokeAsync(() => { }, DispatcherPriority.Background);
                var view = (DictationMicrophoneView)plugin.MountContributions.OfType<DesktopMountView>().Single().CreateView();
                await (Task)Invoke(plugin, "ToggleStatusMicrophoneAsync")!;
                // Start owns a background automatic run. Observe its terminal error,
                // then verify that unrelated idle refreshes cannot erase it.
                await WaitAsync(() => view.Snapshot.Error);
                Invoke(plugin, "RefreshTargetViews"); Invoke(plugin, "RefreshTargetViews");
                Check(view.Snapshot.Error && view.Snapshot.Detail.Contains("Fixture start failure", StringComparison.Ordinal) && !noCapture.Started.Task.IsCompleted,
                    "Pre-capture startup failure stays visible after idle refreshes instead of becoming Off-only feedback.");
                Check(LogicalButtons(view).Any(button => AutomationProperties.GetAutomationId(button) == "AiDictation.OpenControls"),
                    "The one-off popout remains reachable even when automatic startup fails.");
            }
            var opened = 0;
            using var widget = new DictationMicrophoneView(true, () => Task.CompletedTask, () => Task.CompletedTask, open: () => opened++);
            LogicalButtons(widget).Single(button => AutomationProperties.GetAutomationId(button) == "AiDictation.OpenControls").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(opened == 1, "The status popout dispatches its open callback without starting recording.");
        }
        finally
        {
            var resolved = Path.GetFullPath(fixture);
            if (!resolved.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase) ||
                !Path.GetFileName(resolved).StartsWith("cerebral-dictation-manual-", StringComparison.Ordinal)) throw new IOException("Unsafe manual fixture cleanup path.");
            if (Directory.Exists(resolved)) Directory.Delete(resolved, true);
        }
        return checks;
    }

    private sealed class ManualFakePlayback : IDictationPlayback
    {
        internal int Calls;
        internal string? Path;
        public Task PlayAsync(string ownedWavPath, CancellationToken cancellationToken)
        { cancellationToken.ThrowIfCancellationRequested(); Calls++; Path = ownedWavPath; return Task.CompletedTask; }
    }
}
