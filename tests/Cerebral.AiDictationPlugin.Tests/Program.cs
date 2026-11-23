using System.IO;
using System.Text;
using System.Text.Json;
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
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "transcribe") return AiDictationMountChecks.FixtureTranscribe(args);
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var code = 0;
        app.Dispatcher.InvokeAsync(async () =>
        {
            try
            {
                if (args is ["--send-restart-only"] or ["--send-restart-baseline"])
                {
                    var count = await RunSendRestartChecksAsync(app.Dispatcher, args[0] == "--send-restart-baseline");
                    Console.WriteLine(JsonSerializer.Serialize(new { suite = "dictation-send-restart", checks = count,
                        baselineReproduction = args[0] == "--send-restart-baseline",
                        evidence = "deterministic PCM/capture/provider, typed host callbacks and unshown WPF; no live microphone, inference, installed settings, window input or deployment" }));
                    return;
                }
                if (args is ["--startup-readiness-only"])
                {
                    var count = await RunStartupReadinessChecksAsync();
                    Console.WriteLine(JsonSerializer.Serialize(new { suite = "dictation-startup-readiness", checks = count,
                        evidence = "deterministic preference owner, fake capture/provider and unshown WPF; no live settings, microphone, model or inference" }));
                    return;
                }
                if (args is ["--preferences-compatibility-only"])
                {
                    var count = await RunPreferenceCompatibilityChecksAsync();
                    Console.WriteLine(JsonSerializer.Serialize(new { suite = "dictation-preferences-compatibility", checks = count,
                        evidence = "deterministic saved settings and fake owner/capture/provider with actual unshown WPF; no live settings writes or microphone" }));
                    return;
                }
                if (args is ["--settings-capability-compat-only"])
                {
                    var count = await RunSettingsCapabilityCompatibilityChecksAsync();
                    Console.WriteLine(JsonSerializer.Serialize(new { suite = "dictation-settings-capability-compatibility", checks = count,
                        evidence = "deterministic capability replies and fake owner; no live settings, package or runtime writes" }));
                    return;
                }
                if (args is ["--review-only"] or ["--review-only", "--preview", _])
                {
                    var count = await RunReviewChecksAsync(app.Dispatcher, args.Length == 3 ? args[2] : null);
                    Console.WriteLine(JsonSerializer.Serialize(new { suite = "dictation-review", checks = count,
                        evidence = "fake capture/provider/native target plus offscreen WPF; no actual microphone, UIA app access, input or inference" }));
                    return;
                }
                if (args.Length > 1 && args[0] == "--mount-worker")
                {
                    await AiDictationMountChecks.RunInChildAsync(app.Dispatcher, args[1], args.Contains("--shown", StringComparer.Ordinal));
                    return;
                }
                if (args is ["--phrase-only"])
                {
                    var phraseChecks = await RunPhraseChecksAsync(app.Dispatcher);
                    Console.WriteLine(JsonSerializer.Serialize(new { suite = "dictation-continuous-phrases", checks = phraseChecks,
                        evidence = "deterministic PCM/capture/provider and unshown WPF; no microphone, playback, native inference, deployment or package admission" }));
                    return;
                }
                if (args is ["--settings-route-only"])
                {
                    RunSettingsRouteChecks();
                    var guardChecks = await RunSettingsNavigationGuardChecksAsync();
                    guardChecks += await RunStartupAutoPreferenceChecksAsync();
                    Console.WriteLine("AI Dictation navigation and startup preference checks passed (" + guardChecks + "; deterministic host, unshown WPF).");
                    Console.WriteLine("AI Dictation WPF settings route checks passed.");
                    return;
                }
                if (args is ["--package-update-only"])
                {
                    await RunPackageUpdateChecksAsync(app.Dispatcher);
                    return;
                }
                if (args.Length > 0 && args[0] == "--package-removal-only")
                {
                    if (args.Length is not (3 or 4)) throw new ArgumentException("Use a selected removal phase and an owned fixture root.");
                    await RunPackageRemovalChecksAsync(app.Dispatcher, args[1], args[2], args.Length == 4 ? args[3] : null);
                    return;
                }
                var checks = await RunAsync(app.Dispatcher);
                Console.WriteLine(JsonSerializer.Serialize(new { suite = "ai-dictation-lifecycle", evidence = "deterministic capture/provider with actual WPF dispatch", checks }));
                if (args.Contains("--mount", StringComparer.Ordinal) || args.Contains("--shown", StringComparer.Ordinal))
                    await AiDictationMountChecks.RunAsync(app.Dispatcher, args.Contains("--shown", StringComparer.Ordinal));
                if (args.Contains("--capture-smoke", StringComparer.Ordinal)) await CaptureSmokeAsync();
            }
            catch (Exception ex) { Console.Error.WriteLine(ex); code = 1; }
            finally { app.Shutdown(code); }
        });
        app.Run();
        return code;
    }

    private static void RunSettingsRouteChecks()
    {
        var host = new TestHost { Surface = DesktopPluginSurface.Orchestrator,
            DataDirectory = Path.Combine(Path.GetTempPath(), "cerebral-dictation-route-" + Guid.NewGuid().ToString("N")) };
        using var plugin = new AiDictationPlugin();
        plugin.Initialize(host);
        var mounts = plugin.MountContributions;
        if ((IDesktopPlugin)plugin is IDesktopTextEntryPlugin || mounts.OfType<DesktopMountTextEntryAction>().Count() != 1 ||
            mounts.OfType<DesktopMountTextEntryAction>().Any(action => action.Descriptor.Slot != DesktopMountSlots.AnswerActions))
            throw new InvalidOperationException("The explicit dictation action mounts beside question answers; Auto remains in plugin settings.");
        if (plugin.ComposerActions.Count != 0 || mounts.Any(mount => mount.Descriptor.Slot == DesktopMountSlots.ComposerActions) ||
            !mounts.OfType<DesktopMountAction>().Where(action => action.Descriptor.Slot == DesktopMountSlots.Menus)
                .Select(action => action.Descriptor.Id).SequenceEqual(new[] { "dictation-controls-open", "dictation-controls-close" }))
            throw new InvalidOperationException("The floating controls must retain their command IDs in the menu and stay out of the composer.");
        var view = (DictationMicrophoneView)mounts.OfType<DesktopMountView>().Single().CreateView();
        var row = (Panel)view.Child;
        if (row.Children.OfType<CheckBox>().Any() || row.Children.OfType<Button>().Count() != 2 ||
            row.Children.OfType<Button>().Single(button => AutomationProperties.GetAutomationId(button) == "AiDictation.Microphone").Focusable ||
            !row.Children.OfType<Button>().Any(button => AutomationProperties.GetAutomationId(button) == "AiDictation.OpenControls"))
            throw new InvalidOperationException("Status controls retain automatic Start/Stop and a reachable one-off popout.");
        OpenPluginSettingsForCheckAsync(plugin).GetAwaiter().GetResult();
        if (host.SettingsRequests.Count != 1 || host.SettingsRequests[0] != plugin.ConfigurationPanels.Single().Id)
            throw new InvalidOperationException("The advanced settings route must request the plugin's exact Configuration panel.");
        host.SettingsAvailable = false;
        var unavailable = OpenPluginSettingsForCheckAsync(plugin).GetAwaiter().GetResult();
        if (unavailable.State != DesktopOperationState.Rejected || host.SettingsRequests.Count != 1 || unavailable.Message != "Connect before opening settings.")
            throw new InvalidOperationException("An unavailable advanced settings route must retain the host reason without navigating.");
    }

    private static async Task<int> RunStartupAutoPreferenceChecksAsync()
    {
        var checks = 0;
        if (new AiDictationSettings().AutoTranscribeOnSilence) throw new InvalidOperationException("Auto must remain opt-in by default.");
        checks++;
        foreach (var savedAuto in new[] { true, false })
        {
            var host = new TestHost { Surface = DesktopPluginSurface.Orchestrator,
                DataDirectory = Path.Combine(Path.GetTempPath(), "cerebral-dictation-auto-preference-" + Guid.NewGuid().ToString("N")) };
            await host.ExecuteServiceCommandAsync(CompleteSettingsContract.PluginSaveOperation,
                new PluginSettingsTransferSaveRequest(new(AiDictationSettingsPolicy.Document(new AiDictationSettings
                    { AutoTranscribeOnSilence = savedAuto }), 0, OwnerValidated: true)));
            var savedRevision = host.Settings.Revision;
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            host.SettingsReadGate = gate.Task;
            var capture = new FakeCapture(); var providerCalls = 0;
            using var plugin = new AiDictationPlugin(capture, _ => { providerCalls++; throw new InvalidOperationException("No provider should be created."); });
            plugin.Initialize(host);
            var cached = typeof(AiDictationPlugin).GetField("autoTranscribeOnSilence", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
            cached.SetValue(plugin, !savedAuto); // Deliberately stale fixture cache, with no focused target.
            var view = (DictationMicrophoneView)plugin.MountContributions.OfType<DesktopMountView>().Single().CreateView();
            view.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
            gate.SetResult();
            await WaitAsync(() => (bool)cached.GetValue(plugin)! == savedAuto);
            var remounted = (DictationMicrophoneView)plugin.MountContributions.OfType<DesktopMountView>().Single().CreateView();
            remounted.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
            if (plugin.ReadRuntimePreferences().AutoTranscribeOnSilence != savedAuto ||
                ((Panel)remounted.Child).Children.OfType<CheckBox>().Any() || host.Settings.Revision != savedRevision ||
                capture.Started.Task.IsCompleted || providerCalls != 0)
                throw new InvalidOperationException("A saved Auto choice must survive hydration during unmount without saving or recording.");
            checks++;
            using var reopened = new AiDictationPlugin(new FakeCapture(), _ => throw new InvalidOperationException("No provider should be created."));
            reopened.Initialize(host);
            var reopenedView = (DictationMicrophoneView)reopened.MountContributions.OfType<DesktopMountView>().Single().CreateView();
            if (reopened.ReadRuntimePreferences().AutoTranscribeOnSilence != savedAuto ||
                ((Panel)reopenedView.Child).Children.OfType<CheckBox>().Any() || host.Settings.Revision != savedRevision)
                throw new InvalidOperationException("A reopened plugin must display the same saved Auto choice without a preference write.");
            checks++;
        }
        return checks;
    }

    private static async Task<int> RunSettingsCapabilityCompatibilityChecksAsync()
    {
        var checks = 0;
        var policy = new AiDictationSettingsPolicy();
        var saved = AiDictationSettingsPolicy.Document(new AiDictationSettings()) with { SourcePackageVersion = "1.2.6" };
        if (!policy.Admit(saved, null, false).Accepted)
            throw new InvalidOperationException("The retained 1.2.6 settings producer must remain admitted.");
        checks++;
        foreach (var version in new[] { 15, 16 })
        {
            var host = new TestHost { CapabilityTransform = value => value with { CompleteBackupSchemaVersion = version } };
            var client = new Cerebral.PackageSettings.PackageSettingsOwnerClient(host,
                new(AiDictationSettingsPolicy.PluginId, SettingsPreferenceContract.GlobalScope), "1.2.7");
            var state = await client.ReadAsync(CancellationToken.None);
            if (state.Revision != 0 || host.SettingsReadRequests != 1)
                throw new InvalidOperationException("Reviewed complete backup schema must retain the transfer route.");
            checks++;
        }
        var rejected = new Func<CompleteSettingsCapabilities, CompleteSettingsCapabilities>[]
        {
            value => value with { CompleteBackupSchemaVersion = 14 },
            value => value with { CompleteBackupSchemaVersion = 17 },
            value => value with { SchemaVersion = 2 },
            value => value with { PluginEnvelopeVersion = 3 },
            value => value with { PluginGetOperation = "unknown.transfer.get" }
        };
        foreach (var change in rejected)
        {
            var host = new TestHost { CapabilityTransform = change };
            var client = new Cerebral.PackageSettings.PackageSettingsOwnerClient(host,
                new(AiDictationSettingsPolicy.PluginId, SettingsPreferenceContract.GlobalScope), "1.2.7");
            try { _ = await client.ReadAsync(CancellationToken.None); }
            catch (Cerebral.PackageSettings.PackageSettingsUnavailableException error)
                when (error.GetBaseException() is InvalidDataException)
            {
                if (host.SettingsReadRequests != 0 || host.Settings.Revision != 0)
                    throw new InvalidOperationException("Rejected capabilities must not read or save settings.");
                checks++;
                continue;
            }
            throw new InvalidOperationException("An unreviewed settings capability must fail closed.");
        }
        return checks;
    }

    private static async Task<int> RunAsync(Dispatcher dispatcher)
    {
        var checks = 0;
        void Check(bool value, string label) { if (!value) throw new InvalidOperationException(label); checks++; }
        checks += await RunStartupAutoPreferenceChecksAsync();
        var root = NewRoot();
        try
        {
            var host = new TestHost { DataDirectory = Path.Combine(root, "plugin") };
            checks += await RunResumeChecksAsync(dispatcher, root, host);
            var statuses = new List<DesktopTextEntryActivity>();
            AiDictationSession Create(FakeCapture capture, FakeProvider provider) => new(host, root,
                () => new() { NormalizeQuietAudio = false }, _ => provider, capture, action => dispatcher.InvokeAsync(action).Task.Unwrap(), (activity, _) => statuses.Add(activity));

            var capture = new FakeCapture(); var provider = new FakeProvider(); var first = new Entry("composer", dispatcher);
            using (var session = Create(capture, provider))
            {
                await session.ToggleAsync(first, CancellationToken.None); var work = session.Completion;
                await capture.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));
                await session.ToggleAsync(first, CancellationToken.None);
                await provider.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));
                provider.Result.SetResult(Transcript("A real target, fixture text."));
                await work.WaitAsync(TimeSpan.FromSeconds(3));
                Check(first.Insertions.SequenceEqual(new[] { "A real target, fixture text." }), "one insertion into initiating composer");
                Check(statuses.Any(x => x.State == DesktopTextEntryActivityState.Listening) && statuses.Any(x => x.ButtonLabel == "Cancel AI"), "listening and transcribing visible states");
                Check(!statuses[^1].Active && statuses[^1].State == DesktopTextEntryActivityState.Result, "successful session leaves inactive result state");
                Check(Directory.GetFiles(root, "*.wav").Length == 0, "temporary recording removed after success");
            }

            capture = new FakeCapture(); provider = new FakeProvider(); var submitted = new Entry("composer-submit", dispatcher);
            using (var session = Create(capture, provider))
            {
                await session.ToggleAsync(submitted, CancellationToken.None);
                await capture.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));
                var preparation = session.PrepareSubmissionAsync(submitted, CancellationToken.None);
                await provider.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));
                provider.Result.SetResult(Transcript("Finish this before send."));
                var prepared = await preparation.WaitAsync(TimeSpan.FromSeconds(3));
                Check(prepared.State == DesktopOperationState.Completed &&
                      submitted.Insertions.SequenceEqual(new[] { "Finish this before send." }),
                    "submission preparation stops capture, awaits transcription, and inserts text before the host freezes the draft");
            }

            capture = new FakeCapture(); provider = new FakeProvider(); var typedBeforeSilence = new Entry("composer-typed", dispatcher);
            typedBeforeSilence.Insertions.Add("Keep the text I already typed.");
            using (var session = Create(capture, provider))
            {
                await session.ToggleAsync(typedBeforeSilence, CancellationToken.None);
                await capture.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));
                var preparation = session.PrepareSubmissionAsync(typedBeforeSilence, CancellationToken.None);
                await provider.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));
                provider.Result.SetResult(Transcript(""));
                var prepared = await preparation.WaitAsync(TimeSpan.FromSeconds(3));
                Check(prepared.State == DesktopOperationState.Completed && typedBeforeSilence.Text == "Keep the text I already typed.",
                    "automatic dictation silence must not block submission when the composer already contains text");
            }

            capture = new FakeCapture(); provider = new FakeProvider(); var removed = new Entry("answer-old", dispatcher);
            using (var session = Create(capture, provider))
            {
                await session.ToggleAsync(removed, CancellationToken.None); var work = session.Completion;
                await capture.Started.Task.WaitAsync(TimeSpan.FromSeconds(3)); removed.Available.Cancel();
                await work.WaitAsync(TimeSpan.FromSeconds(3));
                Check(provider.Calls == 0 && removed.Insertions.Count == 0, "removed answer cancels capture before inference");
            }

            capture = new FakeCapture(); provider = new FakeProvider(); var cancelled = new Entry("answer-current", dispatcher);
            using (var session = Create(capture, provider))
            {
                await session.ToggleAsync(cancelled, CancellationToken.None); var work = session.Completion;
                await capture.Started.Task.WaitAsync(TimeSpan.FromSeconds(3)); await session.ToggleAsync(cancelled, CancellationToken.None);
                await provider.Started.Task.WaitAsync(TimeSpan.FromSeconds(3)); await session.ToggleAsync(cancelled, CancellationToken.None);
                await work.WaitAsync(TimeSpan.FromSeconds(3));
                Check(provider.Cancelled && cancelled.Insertions.Count == 0, "press during transcription cancels provider and inserts nothing");
            }

            capture = new FakeCapture(); provider = new FakeProvider { IgnoreCancellation = true }; var stale = new Entry("answer-stale", dispatcher);
            using (var session = Create(capture, provider))
            {
                await session.ToggleAsync(stale, CancellationToken.None); var work = session.Completion;
                await capture.Started.Task.WaitAsync(TimeSpan.FromSeconds(3)); await session.ToggleAsync(stale, CancellationToken.None);
                await provider.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));
                host.Binding = new("other-conversation", "second");
                provider.Result.SetResult(Transcript("Must not be inserted."));
                await work.WaitAsync(TimeSpan.FromSeconds(3));
                Check(stale.Insertions.Count == 0, "generation mismatch prevents insertion even without a host cancellation event");
            }

            capture = new FakeCapture(); provider = new FakeProvider(); var old = new Entry("old-target", dispatcher); var replacement = new Entry("replacement-target", dispatcher);
            using (var session = Create(capture, provider))
            {
                await session.ToggleAsync(old, CancellationToken.None);
                await capture.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));
                await session.ToggleAsync(replacement, CancellationToken.None); var work = session.Completion;
                await capture.SecondStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
                await session.ToggleAsync(replacement, CancellationToken.None);
                await provider.Started.Task.WaitAsync(TimeSpan.FromSeconds(3)); provider.Result.SetResult(Transcript("Replacement only."));
                await work.WaitAsync(TimeSpan.FromSeconds(3));
                Check(old.Insertions.Count == 0 && replacement.Insertions.SequenceEqual(new[] { "Replacement only." }), "switching inputs cancels old capture before new capture");
            }

            capture = new FakeCapture(); provider = new FakeProvider(); var unloading = new Entry("unloading", dispatcher);
            var disposed = Create(capture, provider);
            await disposed.ToggleAsync(unloading, CancellationToken.None); var unloadingWork = disposed.Completion;
            await capture.Started.Task.WaitAsync(TimeSpan.FromSeconds(3)); disposed.Dispose();
            await unloadingWork.WaitAsync(TimeSpan.FromSeconds(3));
            Check(provider.Calls == 0 && unloading.Insertions.Count == 0, "plugin disposal cancels capture without insertion");
            Check(Directory.GetFiles(root, "*.wav").Length == 0, "cancelled recordings removed");

            var store = new AiDictationSettingsStore(Path.Combine(root, "settings"));
            var saved = new AiDictationSettings { RuntimePath = @"C:\runtime\nemo-speech.exe", ModelPath = @"C:\models\speech.gguf",
                InputDeviceName = "Fixture microphone", InputBoostDb = 9, NormalizeQuietAudio = false,
                NormalizationCalibrationSeconds = 8, AutoTranscribeOnSilence = true, SilenceWaitSeconds = 2.5 };
            store.Save(saved);
            Check(store.Load() == saved, "explicit model provenance and path settings round trip");
            Check(Directory.GetFiles(Path.Combine(root, "settings"), "*.tmp").Length == 0, "settings atomically leave no temp file");
            Check(host.Submissions == 0, "dictation never submitted or steered a Chat turn");
            var tools = Path.Combine(root, "shared-tools"); var models = Path.Combine(root, "shared-models");
            var discoveredRuntime = Path.Combine(tools, "AI-Dictation", "nemo-speech.exe");
            var discoveredModel = Path.Combine(models, "AI-Dictation", "parakeet-tdt-0.6b-v3.q8_0.gguf");
            Directory.CreateDirectory(Path.GetDirectoryName(discoveredRuntime)!); Directory.CreateDirectory(Path.GetDirectoryName(discoveredModel)!);
            File.WriteAllText(discoveredRuntime, "fixture runtime"); File.WriteAllText(discoveredModel, "fixture model");
            var autoHost = new TestHost
            {
                DataDirectory = Path.Combine(root, "auto-settings"),
                ProductPaths = new(root, root, root, Path.Combine(root, "projects"), models, tools, Path.Combine(root, "shared"))
            };
            using (var autoPlugin = new AiDictationPlugin())
            {
                autoPlugin.Initialize(autoHost);
                var autoView = (ScrollViewer)autoPlugin.ConfigurationPanels.Single().CreateView();
                var autoRuntime = Find<TextBox>(autoView, "AiDictationRuntimePath");
                var autoModel = Find<TextBox>(autoView, "AiDictationModelPath");
                var cpu = Find<CheckBox>(autoView, "AiDictationCpuRequired");
                Check(autoRuntime.Text.Length == 0 && autoModel.Text.Length == 0 && autoHost.Settings.Revision == 0,
                    "opening the preferences reads defaults without silently adopting discovered paths");
                Find<Button>(autoView, "AiDictationPreparePaths").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Check(autoRuntime.Text == discoveredRuntime && autoModel.Text == discoveredModel,
                    "explicit preparation finds the runtime and model in the host's shared Tools and Models roots");
                Check(autoHost.Settings.Revision == 0, "preparing installed paths does not save preferences");
                Check(cpu is { IsChecked: true, IsEnabled: false },
                    "the CPU-only bundled runtime is shown as selected and cannot expose a false GPU option");
                Check(Find<ComboBox>(autoView, "AiDictationInputDevice").Items.Count >= 1 &&
                    Find<Slider>(autoView, "AiDictationSoftwareBoost").Maximum == 24 &&
                    Find<CheckBox>(autoView, "AiDictationNormalizeInput").IsChecked == true &&
                    Find<Slider>(autoView, "AiDictationNormalizationCalibration").Minimum == 5 &&
                    Find<CheckBox>(autoView, "AiDictationAutoTranscribe").IsChecked == false &&
                    Find<Slider>(autoView, "AiDictationSilenceWait").Maximum == 10,
                    "input selector, bounded processing, calibration and opt-in silence transcription are mounted in Configuration");
            }
            using var plugin = new AiDictationPlugin();
            Check(plugin.TextEntryActions.Select(action => action.Id).Order().SequenceEqual(new[] { "ai-dictate", "ai-dictate-auto" }) &&
                plugin.ConfigurationPanels.Single().Id == "ai-dictation-settings", "specific removable plugin actions and panel identity");
            plugin.Initialize(host);
            var settingsView = (ScrollViewer)plugin.ConfigurationPanels.Single().CreateView();
            var runtimeBox = Find<TextBox>(settingsView, "AiDictationRuntimePath");
            var beforeEdit = host.Settings;
            runtimeBox.Text = @"C:\runtime\selected-nemo.exe";
            Check(host.Settings == beforeEdit && plugin.ReadRuntimePreferences().RuntimePath != runtimeBox.Text,
                "editing a draft does not change saved or runtime preferences");
            Find<Button>(settingsView, "AiDictationSavePreferences").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(host.Settings.Revision == beforeEdit.Revision + 1 && host.Settings.Active is { } active &&
                AiDictationSettingsPolicy.Decode(active.Values).RuntimePath == runtimeBox.Text &&
                plugin.ReadRuntimePreferences().RuntimePath == runtimeBox.Text &&
                !File.Exists(Path.Combine(host.DataDirectory, "ai-dictation.settings.json")),
                "explicit Save commits the scoped Worker preference and leaves the legacy file absent");
            var gainSamples = new byte[6];
            System.Buffers.Binary.BinaryPrimitives.WriteInt16LittleEndian(gainSamples.AsSpan(0, 2), 1000);
            System.Buffers.Binary.BinaryPrimitives.WriteInt16LittleEndian(gainSamples.AsSpan(2, 2), -1000);
            System.Buffers.Binary.BinaryPrimitives.WriteInt16LittleEndian(gainSamples.AsSpan(4, 2), 20000);
            var gainPeak = WinMmWaveCapture.ApplySoftwareGain(gainSamples, 6);
            Check(gainPeak > .99 && System.Buffers.Binary.BinaryPrimitives.ReadInt16LittleEndian(gainSamples) is > 1900 and < 2100,
                "software boost scales PCM16 and clips to the valid sample range");
            var quietSamples = new byte[4];
            System.Buffers.Binary.BinaryPrimitives.WriteInt16LittleEndian(quietSamples.AsSpan(0, 2), 1000);
            System.Buffers.Binary.BinaryPrimitives.WriteInt16LittleEndian(quietSamples.AsSpan(2, 2), -2000);
            var normalized = WinMmWaveCapture.NormalizePcm16(quietSamples);
            Check(normalized.Applied && normalized.GainDb <= 18 && normalized.ResultPeak > normalized.SourcePeak,
                "quiet PCM16 is normalized toward the safe target with bounded gain");
            Check(plugin.ComposerActions.Count == 0 && plugin.Panels.Count == 0,
                "dictation exposes no legacy composer actions or sidebar rows");

            var repeatingCapture = new ControlledCapture();
            var repeatingProvider = new SequenceProvider(" first", " second");
            var focusHost = new TestHost { DataDirectory = Path.Combine(root, "focus-settings"), Surface = DesktopPluginSurface.Orchestrator };
            using (var focusPlugin = new AiDictationPlugin(repeatingCapture, _ => repeatingProvider))
            {
                focusPlugin.Initialize(focusHost);
                var focused = new FocusEntry("focus-composer", dispatcher, focusHost) { Value = "AB", Caret = 1 };
                var automatic = focusPlugin.TextEntryActions.Single(action => action.Id == "ai-dictate-auto");
                await automatic.ExecuteAsync(focused, CancellationToken.None);
                await repeatingCapture.WaitStartedAsync(1);
                repeatingCapture.Complete(1);
                await repeatingCapture.WaitStartedAsync(2);
                Check(focused.Value == "A first B", "automatic dictation separates a captured segment from existing words on both sides");
                repeatingCapture.Complete(2);
                await repeatingCapture.WaitStartedAsync(3);
                Check(focused.Value == "A first second B", "automatic dictation repeats without deleting the previous segment");
                focusHost.Focus(null);
                await WaitAsync(() => repeatingCapture.Cancelled > 0);
                var pausedText = focused.Value;
                await Task.Delay(175);
                Check(focused.Value == pausedText && repeatingProvider.Calls == 2,
                    "leaving editable focus cancels capture and prevents background insertion");
                Check(focused.Activities.Any(activity => activity.Message.Contains("paused", StringComparison.OrdinalIgnoreCase)),
                    "focus loss publishes an automatic-dictation paused state");
            }
            checks += await RunInsertionChecksAsync(dispatcher, root, host);
            return checks;
        }
        finally { RemoveRoot(root); }
    }

    private static async Task CaptureSmokeAsync()
    {
        var devices = WinMmWaveCapture.InputDeviceCount;
        if (devices == 0) throw new InvalidOperationException("No Windows microphone is available for the explicit native capture test.");
        var root = NewRoot();
        try
        {
            var capture = new WinMmWaveCapture();
            using var finish = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            var updates = 0;
            var options = new AiDictationCaptureOptions("", 0);
            var captured = await capture.CaptureAsync(Path.Combine(root, "microphone.wav"), options, finish.Token, cancel.Token, _ => Interlocked.Increment(ref updates)).WaitAsync(TimeSpan.FromSeconds(10));
            if (captured.DurationSeconds <= 0 || captured.DurationSeconds > 3 || updates < 1) throw new InvalidOperationException("Native capture returned no bounded audio evidence.");
            using var stop = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
            try { await capture.CaptureAsync(Path.Combine(root, "cancelled.wav"), options, CancellationToken.None, stop.Token, _ => { }).WaitAsync(TimeSpan.FromSeconds(5)); throw new InvalidOperationException("Expected native cancellation."); }
            catch (OperationCanceledException) { }
            using var secondFinish = new CancellationTokenSource(TimeSpan.FromMilliseconds(350));
            var resumed = await capture.CaptureAsync(Path.Combine(root, "reopened.wav"), options, secondFinish.Token, cancel.Token, _ => { }).WaitAsync(TimeSpan.FromSeconds(5));
            if (resumed.DurationSeconds <= 0) throw new InvalidOperationException("Microphone could not reopen after cancellation.");
            Console.WriteLine(JsonSerializer.Serialize(new { suite = "ai-dictation-native-capture", evidence = "actual default Windows microphone capture and reopen, no transcription or UI insertion", devices, seconds = captured.DurationSeconds, levelUpdates = updates, reopenedSeconds = resumed.DurationSeconds }));
        }
        finally { RemoveRoot(root); }
    }

    private static SpeechTranscript Transcript(string text) => new(text, 1, 1, "fixture", "fixture");
    private static string NewRoot()
    {
        var parent = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../tests/.scratch"));
        var root = Path.Combine(parent, "ai-dictation-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root); return root;
    }
    private static void RemoveRoot(string root)
    {
        var parent = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../tests/.scratch"));
        if (!Path.GetFullPath(root).StartsWith(parent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new IOException("Unsafe test cleanup target.");
        Directory.Delete(root, true);
    }

    private static async Task WaitAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition()) await Task.Delay(20, timeout.Token);
    }

    private sealed class FakeCapture : IAiDictationCapture
    {
        public AiDictationCaptureOptions? LastOptions { get; private set; }
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource SecondStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int calls;
        public async Task<AiDictationCaptureResult> CaptureAsync(string path, AiDictationCaptureOptions options, CancellationToken finish, CancellationToken cancel, Action<double> level)
        {
            LastOptions = options;
            await File.WriteAllTextAsync(path, "fixture recording", cancel); level(.25);
            if (Interlocked.Increment(ref calls) == 1) Started.TrySetResult(); else SecondStarted.TrySetResult();
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(finish, cancel);
            try { await Task.Delay(Timeout.InfiniteTimeSpan, linked.Token); }
            catch (OperationCanceledException) when (!cancel.IsCancellationRequested) { }
            cancel.ThrowIfCancellationRequested(); return new(1, new(false, .25, .25, 0));
        }
    }

    private sealed class ControlledCapture : IAiDictationCapture
    {
        private readonly System.Collections.Concurrent.ConcurrentDictionary<int, TaskCompletionSource<bool>> started = new();
        private readonly System.Collections.Concurrent.ConcurrentDictionary<int, TaskCompletionSource<bool>> release = new();
        private int calls;
        public int Cancelled { get; private set; }
        private static TaskCompletionSource<bool> Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task WaitStartedAsync(int index) => started.GetOrAdd(index, _ => Signal()).Task.WaitAsync(TimeSpan.FromSeconds(5));
        public void Complete(int index) => release.GetOrAdd(index, _ => Signal()).TrySetResult(true);
        public async Task<AiDictationCaptureResult> CaptureAsync(string path, AiDictationCaptureOptions options,
            CancellationToken finish, CancellationToken cancel, Action<double> level)
        {
            var index = Interlocked.Increment(ref calls);
            await File.WriteAllTextAsync(path, "controlled recording", cancel);
            level(.3); started.GetOrAdd(index, _ => Signal()).TrySetResult(true);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(finish, cancel);
            try { await release.GetOrAdd(index, _ => Signal()).Task.WaitAsync(linked.Token); }
            catch (OperationCanceledException) when (cancel.IsCancellationRequested)
            {
                Cancelled++; throw;
            }
            cancel.ThrowIfCancellationRequested();
            return new(1, new(false, .3, .3, 0));
        }
    }

    private static T Find<T>(DependencyObject root, string automationId) where T : FrameworkElement
    {
        if (root is T match && AutomationProperties.GetAutomationId(match) == automationId) return match;
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {
            try { return Find<T>(child, automationId); }
            catch (KeyNotFoundException) { }
        }
        throw new KeyNotFoundException(automationId);
    }
    private sealed class FakeProvider : ISpeechProvider
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<SpeechTranscript> Result { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool IgnoreCancellation { get; init; }
        public bool Cancelled { get; private set; }
        public int Calls { get; private set; }
        public SpeechProviderDescriptor Descriptor => new("fixture", "1", "fixture", false);
        public SpeechProviderAvailability Inspect(SpeechModelBinding model) => new(true, null);
        public async Task<SpeechTranscript> TranscribeAsync(SpeechAudioInput audio, SpeechModelBinding model, CancellationToken ct)
        {
            Calls++; Started.TrySetResult();
            try { return IgnoreCancellation ? await Result.Task : await Result.Task.WaitAsync(ct); }
            catch (OperationCanceledException) { Cancelled = true; throw; }
        }
    }
    private sealed class SequenceProvider(params string[] values) : ISpeechProvider
    {
        private readonly Queue<string> results = new(values);
        public int Calls { get; private set; }
        public SpeechProviderDescriptor Descriptor => new("fixture-sequence", "1", "fixture", false);
        public SpeechProviderAvailability Inspect(SpeechModelBinding model) => new(true, null);
        public Task<SpeechTranscript> TranscribeAsync(SpeechAudioInput audio, SpeechModelBinding model, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested(); Calls++;
            if (results.Count == 0) throw new InvalidOperationException("Unexpected extra transcription.");
            return Task.FromResult(Transcript(results.Dequeue()));
        }
    }
    private sealed class Entry(string id, Dispatcher dispatcher) : IDesktopTextEntry, IDesktopTextEntryLifetime
    {
        public CancellationTokenSource Available { get; } = new();
        public List<string> Insertions { get; } = [];
        public string Id => id;
        public string Purpose => id;
        public string Text => string.Join("", Insertions);
        public int SelectionStart => Text.Length;
        public int SelectionLength => 0;
        public CancellationToken AvailabilityToken => Available.Token;
        public Task<DesktopOperationResult> InsertAsync(string text, CancellationToken cancellationToken)
        {
            if (!dispatcher.CheckAccess()) throw new InvalidOperationException("Insertion left the UI owner.");
            cancellationToken.ThrowIfCancellationRequested(); Available.Token.ThrowIfCancellationRequested();
            Insertions.Add(text); return Task.FromResult(new DesktopOperationResult(DesktopOperationState.Completed));
        }
    }
    private sealed class FocusEntry(string id, Dispatcher dispatcher, TestHost host) :
        IDesktopTextEntry, IDesktopTextEntryLifetime, IDesktopTextEntryActivitySink, IDesktopTextEntryFocus
    {
        public CancellationTokenSource Available { get; } = new();
        public List<DesktopTextEntryActivity> Activities { get; } = [];
        public string Value { get; set; } = "";
        public int Caret { get; set; }
        public string Id => id;
        public string Purpose => id;
        public string Text => Value;
        public int SelectionStart => Caret;
        public int SelectionLength => 0;
        public CancellationToken AvailabilityToken => Available.Token;
        public bool IsKeyboardFocusWithin => ReferenceEquals(host.FocusedTextEntry, this);
        public Task<DesktopOperationResult> FocusAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested(); host.Focus(this);
            return Task.FromResult(new DesktopOperationResult(DesktopOperationState.Completed));
        }
        public Task<DesktopOperationResult> InsertAsync(string text, CancellationToken cancellationToken = default)
        {
            if (!dispatcher.CheckAccess()) throw new InvalidOperationException("Insertion left the UI owner.");
            cancellationToken.ThrowIfCancellationRequested(); Available.Token.ThrowIfCancellationRequested();
            Value = Value.Insert(Caret, text); Caret += text.Length;
            return Task.FromResult(new DesktopOperationResult(DesktopOperationState.Completed));
        }
        public void ReportActivity(DesktopTextEntryActivity activity) => Activities.Add(activity);
    }
    internal sealed class TestHost : IDesktopPluginHost, IDesktopServiceHost, IDesktopPluginEnvironment, IDesktopProductEnvironment, IDesktopTextEntryFocusHost,
        IDesktopPluginSettingsNavigationHost, IDesktopDefaultTextEntryHost
    {
        private static readonly PluginSettingsCoordinate SettingsCoordinate = new(AiDictationSettingsPolicy.PluginId, SettingsPreferenceContract.GlobalScope);
        public IDesktopTextEntry? DefaultTextEntry { get; set; }
        public bool SupportsDraftPresentation { get; set; }
        public Func<DesktopDraftSnapshot>? DraftReader { get; set; }
        public Func<DesktopDraftUpdate, CancellationToken, Task<DesktopOperationResult>>? DraftWriter { get; set; }
        private static readonly JsonSerializerOptions SettingsJson = new(JsonSerializerDefaults.Web);
        public PluginSettingsState Settings { get; private set; } = new(SettingsCoordinate, 0, null, [], "none");
        public Task? SettingsReadGate { get; set; }
        public int SettingsReadRequests { get; private set; }
        public Func<CompleteSettingsCapabilities, CompleteSettingsCapabilities>? CapabilityTransform { get; set; }

        public async Task<DesktopServiceCommandResult> ExecuteServiceCommandAsync(string operation, object arguments,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (operation == CompleteSettingsContract.PluginGetOperation) SettingsReadRequests++;
            if (operation == CompleteSettingsContract.PluginGetOperation && SettingsReadGate is { } gate)
                await gate.WaitAsync(cancellationToken);
            object result = operation switch
            {
                CompleteSettingsContract.DescribeOperation => DescribeCapabilities(),
                CompleteSettingsContract.PluginGetOperation when Equals(arguments, SettingsCoordinate) => new PluginSettingsTransferState(Settings, []),
                CompleteSettingsContract.PluginSaveOperation when arguments is PluginSettingsTransferSaveRequest request => SaveSettings(request.Save),
                _ => throw new InvalidOperationException("Unexpected dictation fixture operation or coordinate: " + operation)
            };
            return new DesktopServiceCommandResult(true, JsonSerializer.SerializeToElement(result, SettingsJson));
        }

        private CompleteSettingsCapabilities DescribeCapabilities()
        {
            var value = new CompleteSettingsCapabilities(CompleteSettingsContract.SchemaVersion,
                ConfigurationBackupContract.SchemaVersion, CompleteSettingsContract.BackupSchemaVersion,
                [SettingsTransferPurpose.Portable, SettingsTransferPurpose.Complete], "configuration.presets.export", "configuration.backup.export",
                CompleteSettingsContract.PluginGetOperation, CompleteSettingsContract.PluginSaveOperation);
            return CapabilityTransform?.Invoke(value) ?? value;
        }

        private PluginSettingsTransferSaveResult SaveSettings(PluginSettingsSaveRequest request)
        {
            if (request.Document.Coordinate != SettingsCoordinate || request.ExpectedRevision != Settings.Revision ||
                !request.OwnerValidated || request.PendingId is not null)
                throw new InvalidOperationException("Dictation fixture settings owner or revision mismatch.");
            var changed = Settings.Active is null || !JsonElement.DeepEquals(Settings.Active.Values, request.Document.Values);
            if (changed)
                Settings = new(SettingsCoordinate, Settings.Revision + 1, request.Document, [], "owner-client",
                    request.PortableValues, request.OmittedPortableFields);
            return new(new(Settings, []), changed);
        }

        public DesktopPluginSurface Surface { get; init; } = DesktopPluginSurface.Configuration;
        public string DataDirectory { get; init; } = "";
        public string PackageDirectory => AppContext.BaseDirectory;
        public DesktopProductPaths ProductPaths { get; init; } = new("", "", "", "", "", "", "");
        public DesktopConversationBinding Binding { get; set; } = new("first", "first");
        public int Submissions { get; private set; }
        public bool SettingsAvailable { get; set; } = true;
        public List<string> SettingsRequests { get; } = [];
        public Func<string, DesktopSettingsNavigationAvailability>? SettingsAvailabilityReader { get; set; }
        public Func<string, CancellationToken, Task<DesktopOperationResult>>? SettingsResponder { get; set; }
        public DesktopSettingsNavigationAvailability GetSettingsNavigationAvailability(string panelId) =>
            SettingsAvailabilityReader?.Invoke(panelId) ?? (SettingsAvailable ? new(true) : new(false, "Connect before opening settings."));
        public Task<DesktopOperationResult> OpenSettingsAsync(string panelId, CancellationToken cancellationToken = default)
        {
            SettingsRequests.Add(panelId);
            return SettingsResponder?.Invoke(panelId, cancellationToken) ?? Task.FromResult(new DesktopOperationResult(DesktopOperationState.Completed));
        }
        public IDesktopTextEntry? FocusedTextEntry { get; private set; }
        public event EventHandler<DesktopConversationChangedEventArgs>? ConversationChanged { add { } remove { } }
        public event EventHandler<DesktopLeadMessageEventArgs>? LeadMessageCommitted { add { } remove { } }
        public event EventHandler<DesktopTextEntryFocusChangedEventArgs>? FocusedTextEntryChanged;
        public void Focus(IDesktopTextEntry? entry)
        {
            if (ReferenceEquals(FocusedTextEntry, entry)) return;
            FocusedTextEntry = entry; FocusedTextEntryChanged?.Invoke(this, new(entry));
        }
        public DesktopConversationSnapshot GetConversation() => new(Binding, "fixture", null, null, false);
        public DesktopDraftSnapshot GetDraft() => DraftReader?.Invoke() ?? new(Binding, 0, "");
        public Task<DesktopOperationResult> ReplaceDraftAsync(DesktopDraftUpdate update, CancellationToken cancellationToken = default) =>
            DraftWriter?.Invoke(update, cancellationToken) ?? throw new InvalidOperationException("This fixture does not admit draft replacement.");
        public Task<DesktopOperationResult> SubmitAsync(DesktopSubmission submission, CancellationToken cancellationToken = default) { Submissions++; throw new InvalidOperationException("Dictation must not submit a conversation."); }
        public Task<DesktopOperationResult> SteerAsync(DesktopSubmission submission, CancellationToken cancellationToken = default) { Submissions++; throw new InvalidOperationException("Dictation must not steer a conversation."); }
        public Task<DesktopOperationResult> StopAsync(DesktopConversationBinding binding, CancellationToken cancellationToken = default) => throw new InvalidOperationException("Dictation must not stop a conversation.");
        public void DismissInputGuidance() { }
        public void ReportStatus(string text) { }
        public void RefreshPresentation() { }
    }
}
