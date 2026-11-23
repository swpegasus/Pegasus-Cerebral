using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Cerebral.Desktop.Plugins;

internal static class AiDictationMountChecks
{
    private const string PluginId = "pegasus.ai-dictation";
    private const string FixtureText = "This is deterministic fixture transcription in the original input.";

    // Explicitly configured test executable; product code never selects it or falls back to fixture transcription.
    public static int FixtureTranscribe(string[] args)
    {
        if (args.Length < 2 || !Path.IsPathFullyQualified(args[1])) return 2;
        using var stream = File.OpenRead(args[1]);
        using var reader = new BinaryReader(stream);
        stream.Position = 40;
        var bytes = reader.ReadInt32();
        Console.WriteLine(JsonSerializer.Serialize(new { file = args[1], text = FixtureText, duration = bytes / 32000d }));
        return 0;
    }

    public static async Task RunAsync(Dispatcher dispatcher, bool shown)
    {
        dispatcher.VerifyAccess();
        var repository = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../.."));
        var scratch = Path.Combine(repository, "tests", ".scratch");
        var root = Path.Combine(scratch, "ai-dictation-mount-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var start = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "Cerebral.AiDictationPlugin.Tests.exe"))
        {
            UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        start.ArgumentList.Add("--mount-worker"); start.ArgumentList.Add(root);
        if (shown) start.ArgumentList.Add("--shown");
        using var child = new Process { StartInfo = start };
        try
        {
            if (!child.Start()) throw new IOException("The owned native mount fixture did not start.");
            var output = child.StandardOutput.ReadToEndAsync(); var error = child.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            try { await child.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException)
            {
                child.Kill(entireProcessTree: true);
                await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
                throw new TimeoutException("The native mount fixture exceeded its time limit.");
            }
            var stdout = await output; var stderr = await error;
            if (child.ExitCode != 0) throw new InvalidOperationException("Native mount fixture failed: " + stderr);
            Console.Write(stdout);
        }
        finally
        {
            // Native plugin contexts are cooperative. Cleanup waits for the owning process, never deletes mapped DLLs.
            if (!Path.GetFullPath(root).StartsWith(Path.GetFullPath(scratch) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new IOException("Unsafe mount cleanup target.");
            Directory.Delete(root, true);
        }
    }

    public static async Task RunInChildAsync(Dispatcher dispatcher, string root, bool shown)
    {
        var count = 0;
        void Check(bool value, string label) { if (!value) throw new InvalidOperationException(label); count++; }
        var repository = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../.."));
        var scratch = Path.Combine(repository, "tests", ".scratch");
        if (!Path.IsPathFullyQualified(root) || !Path.GetFullPath(root).StartsWith(scratch + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || !Path.GetFileName(root).StartsWith("ai-dictation-mount-", StringComparison.Ordinal) || !Directory.Exists(root))
            throw new IOException("Native mount fixture requires its exact owned scratch root.");
        Window? window = null;
        DesktopPluginRuntime? configuration = null;
        DesktopPluginRuntime? runtime = null;
        var binding = new DesktopConversationBinding("dictation-mount", "fixture");
        var errors = new List<string>();
        var data = Path.Combine(root, "data");
        var submitted = 0;
        try
        {
            // Use the plugin built with this test executable, including for custom output paths.
            // Keep the test host and shared application assemblies out of the removable package.
            var source = Path.Combine(root, "package-source");
            Directory.CreateDirectory(source);
            foreach (var leaf in new[] { DesktopPluginCatalog.ManifestFileName, "Cerebral.AiDictationPlugin.dll",
                "Cerebral.AiDictationPlugin.deps.json", "Cerebral.Speech.dll" })
                File.Copy(Path.Combine(AppContext.BaseDirectory, leaf), Path.Combine(source, leaf));
            Check(File.Exists(Path.Combine(source, "Cerebral.Speech.dll")), "speech provider DLL is present in removable package");
            Check(!Directory.EnumerateFiles(source, "*.gguf", SearchOption.AllDirectories).Any()
                && !Directory.EnumerateFiles(source, "nemo-speech.exe", SearchOption.AllDirectories).Any(), "runtime and model weights are not bundled");
            var catalog = new DesktopPluginCatalog(Path.Combine(root, "packages"));
            var installed = catalog.Install(Path.Combine(source, DesktopPluginCatalog.ManifestFileName));
            Check(installed.Id == PluginId && !installed.Enabled, "isolated package install begins disabled");
            catalog.SetEnabled(PluginId, true);
            var isolatedPaths = new DesktopProductPaths(root, data, data, Path.Combine(root, "projects"), Path.Combine(root, "models"),
                Path.Combine(root, "tools"), Path.Combine(root, "shared"));
            var preferences = new Program.TestHost();
            var configurationHost = new DesktopPluginHostAdapter(dispatcher, errors.Add, data,
                serviceCommand: preferences.ExecuteServiceCommandAsync, productPaths: () => isolatedPaths);
            configuration = new(catalog, configurationHost, dispatcher);
            configuration.LoadEnabled();
            Check(configuration.Status.Single() is { Id: PluginId, Loaded: true, Error: null }, "actual native Configuration runtime loads package");
            Check(configuration.Panels.Count == 1 && configuration.TextEntryActions.Count == 0, "Configuration mounts only settings panel");
            var settingsPanel = configuration.Panels.Single().View;
            var host = new DesktopPluginHostAdapter(dispatcher,
                () => new(binding, "AI Dictation fixture", null, null, false), () => new(binding, 0, ""),
                (_, _) => throw new InvalidOperationException("Dictation must not replace a draft."),
                (_, _) => { submitted++; throw new InvalidOperationException("Dictation must not submit."); },
                (_, _) => { submitted++; throw new InvalidOperationException("Dictation must not steer."); },
                (_, _) => throw new InvalidOperationException("Dictation must not stop Chat."), errors.Add, DesktopPluginSurface.Orchestrator, data,
                serviceCommand: preferences.ExecuteServiceCommandAsync, productPaths: () => isolatedPaths);
            runtime = new(catalog, host, dispatcher);
            runtime.LoadEnabled();
            Check(runtime.Status.Single() is { Id: PluginId, Loaded: true, Error: null }, "actual native Orchestrator runtime loads package");
            Check(runtime.TextEntryActions.Count == 2 && runtime.Panels.Count == 0 && runtime.InputModes.Count == 0 &&
                runtime.StatusItems.Count == 1 && runtime.ActionsFor(DesktopMountSlots.ComposerActions).Count == 0,
                "Orchestrator mounts status-bar dictation and question actions without composer rows");
            Check(runtime.ActionsFor(DesktopMountSlots.Menus).Select(item => item.Id)
                .SequenceEqual(new[] { "dictation-controls-open", "dictation-controls-close" }) &&
                runtime.ComposerActions.Any(item => item.Id == "dictation-controls-open"),
                "floating dictation actions use the menu and remain available by their established command IDs");
            Check(runtime.TextEntrySubmissionPreparations.Single().PluginId == PluginId,
                "Orchestrator discovers the packaged AI Dictation pre-submit participant");
            var action = runtime.TextEntryActions.Single(item => item.Id == "ai-dictate");
            Check(action.Id == "ai-dictate" && action.PluginId == PluginId, "mounted action retains specific identity");

            var tabs = new TabControl();
            var settingsTab = new TabItem { Header = "AI Dictation settings", Content = settingsPanel };
            tabs.Items.Add(settingsTab);
            var inputs = new StackPanel { Margin = new Thickness(24) };
            inputs.Children.Add(new TextBlock { Text = "Isolated AI Dictation acceptance", FontSize = 22 });
            inputs.Children.Add(new TextBlock { Text = "Actual mounted plugin and microphone capture; deterministic test transcription. No person is claimed to have spoken.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 20) });
            var composer = new MountedEntry("composer", inputs, action);
            var answer = new MountedEntry("answer", inputs, action);
            Check((await runtime.TextEntrySubmissionPreparations.Single().PrepareAsync(composer, CancellationToken.None)).State == DesktopOperationState.Completed,
                "inactive packaged AI Dictation preparation leaves the composer ready to submit");
            var inputsTab = new TabItem { Header = "Shown inputs", Content = inputs };
            tabs.Items.Add(inputsTab);
            window = new Window { Title = "AI Dictation acceptance · fixture transcription", Width = 1024, Height = 768, Content = tabs,
                WindowStartupLocation = WindowStartupLocation.CenterScreen };
            if (shown) { window.Show(); window.Activate(); await dispatcher.InvokeAsync(() => window.UpdateLayout(), DispatcherPriority.ApplicationIdle); }
            else { tabs.Measure(new Size(960, 650)); tabs.Arrange(new Rect(0, 0, 960, 650)); tabs.UpdateLayout(); }
            var runtimeBox = (TextBox)Find(settingsPanel, "AiDictationRuntimePath");
            var modelBox = (TextBox)Find(settingsPanel, "AiDictationModelPath");
            Check(runtimeBox.Text.Length == 0 && modelBox.Text.Length == 0, "mounted default configuration has no implicit assets");
            Check(!File.Exists(Path.Combine(data, PluginId, "ai-dictation.settings.json")), "mounting unconfigured plugin does not create settings");
            if (shown)
            {
                Check(window.IsVisible && new WindowInteropHelper(window).Handle != IntPtr.Zero && settingsPanel.IsVisible, "actual settings panel shown in owned native window");
                Check(runtimeBox.ActualWidth > 0 && runtimeBox.ActualWidth < window.ActualWidth, "settings fields have bounded visible layout");
            }
            tabs.SelectedItem = inputsTab; tabs.UpdateLayout();
            await composer.ClickAsync();
            Check(composer.LastActivity is { State: DesktopTextEntryActivityState.Error, Active: false } && composer.Box.Text.Length == 0,
                "unconfigured mounted action reports error without capture or insertion");
            if (shown)
            {
                tabs.SelectedItem = settingsTab; tabs.UpdateLayout();
                var model = Path.Combine(root, "fixture.gguf"); File.WriteAllBytes(model, [0, 1, 2, 3]);
                runtimeBox.Text = Path.Combine(AppContext.BaseDirectory, "Cerebral.AiDictationPlugin.Tests.exe"); modelBox.Text = model;
                ((TextBox)Find(settingsPanel, "AiDictationModelId")).Text = "fixture-transcription";
                ((TextBox)Find(settingsPanel, "AiDictationModelRevision")).Text = "acceptance-only";
                ((TextBox)Find(settingsPanel, "AiDictationModelSource")).Text = "https://example.invalid/fixture";
                ((TextBox)Find(settingsPanel, "AiDictationModelLicense")).Text = "Fixture only";
                ((Button)Find(settingsPanel, "AiDictationSavePreferences")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Check(preferences.Settings.Active is not null && preferences.Settings.Revision == 1,
                    "shown fixture preferences were explicitly saved before recording");
                tabs.SelectedItem = inputsTab; tabs.UpdateLayout();
                Check(composer.Box.IsVisible && answer.Box.IsVisible && composer.Button.IsVisible, "actual composer and answer controls are shown");
                Check(composer.Box.ActualWidth > 300 && answer.Box.ActualWidth <= inputs.ActualWidth, "input controls fit1024x768 fixture");
                await composer.ClickAsync();
                await WaitAsync(() => composer.LastActivity?.State == DesktopTextEntryActivityState.Listening);
                await Task.Delay(500);
                await composer.ClickAsync();
                await WaitAsync(() => composer.LastActivity is { Active: false, State: DesktopTextEntryActivityState.Result });
                Check(composer.Box.Text == FixtureText && answer.Box.Text.Length == 0, "mounted action inserts fixture transcript only into initiating shown composer");
                await answer.ClickAsync();
                await WaitAsync(() => answer.LastActivity?.State == DesktopTextEntryActivityState.Listening);
                await Task.Delay(400); await answer.ClickAsync();
                await WaitAsync(() => answer.LastActivity is { Active: false, State: DesktopTextEntryActivityState.Result });
                Check(answer.Box.Text == FixtureText && composer.Box.Text == FixtureText, "mounted action inserts separately into shown answer without changing composer");
                SaveShownContent(tabs, Path.Combine(repository, ".runtime", "acceptance", "ai-dictation-current.png"));
                answer.Box.Text = "Protected answer";
                await answer.ClickAsync(); await WaitAsync(() => answer.LastActivity?.State == DesktopTextEntryActivityState.Listening);
                answer.Available.Cancel();
                await WaitAsync(() => answer.LastActivity is { Active: false });
                Check(answer.Box.Text == "Protected answer", "removing an active shown answer prevents late insertion");
                await composer.ClickAsync(); await WaitAsync(() => composer.LastActivity?.State == DesktopTextEntryActivityState.Listening);
                catalog.SetEnabled(PluginId, false); runtime.LoadEnabled();
                await WaitAsync(() => composer.LastActivity is { Active: false });
                Check(composer.Box.Text == FixtureText, "unmount during actual recording cancels without appending text");
                await WaitAsync(() => !Directory.Exists(Path.Combine(data, PluginId, "ai-dictation-capture"))
                    || !Directory.EnumerateFiles(Path.Combine(data, PluginId, "ai-dictation-capture"), "*.wav").Any());
                Check(true, "mounted session temporary captures were removed");
            }
            catalog.SetEnabled(PluginId, false); runtime.LoadEnabled(); configuration.LoadEnabled();
            Check(runtime.TextEntryActions.Count == 0 && runtime.TextEntrySubmissionPreparations.Count == 0 && runtime.Status.Count == 0 && configuration.Panels.Count == 0,
                "disable removes all native contributions");
            try { await action.ExecuteAsync(composer, CancellationToken.None); throw new InvalidOperationException("Expected stale action rejection."); }
            catch (InvalidOperationException ex) when (ex.Message.Contains("no longer enabled", StringComparison.Ordinal)) { Check(true, "saved action cannot run after native unmount"); }
            Check(submitted == 0, "mounted workflow never creates or steers a Chat request");
            Check(errors.Count == 0, "native package runtime reported no load/dispatch errors");
            var dpi = window.IsVisible ? VisualTreeHelper.GetDpi(window).DpiScaleX : 0;
            Console.WriteLine(JsonSerializer.Serialize(new { suite = "ai-dictation-native-mount", checks = count, shown,
                evidence = shown ? "actual native package mount, shown WPF controls and microphone; explicitly configured deterministic transcription" : "actual native package mount/unmount; no microphone or shown-window claim",
                width = shown ? window.ActualWidth : 0, height = shown ? window.ActualHeight : 0, dpiScale = dpi }));
        }
        finally
        {
            runtime?.Dispose(); configuration?.Dispose();
            if (window is not null) { window.Content = null; window.Close(); }
        }
    }

    private static FrameworkElement Find(DependencyObject root, string id)
    {
        if (root is FrameworkElement element && AutomationProperties.GetAutomationId(element) == id) return element;
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {
            try { return Find(child, id); }
            catch (KeyNotFoundException) { }
        }
        throw new KeyNotFoundException(id);
    }
    private static void SaveShownContent(FrameworkElement content, string path)
    {
        if (!content.IsVisible || PresentationSource.FromVisual(content) is null) throw new InvalidOperationException("Capture requires the actual shown test control.");
        content.UpdateLayout();
        var dpi = VisualTreeHelper.GetDpi(content);
        var width = checked((int)Math.Ceiling(content.ActualWidth * dpi.DpiScaleX));
        var height = checked((int)Math.Ceiling(content.ActualHeight * dpi.DpiScaleY));
        if (width is < 1 or > 4096 || height is < 1 or > 4096) throw new InvalidOperationException("Shown capture exceeds bounds.");
        var bitmap = new RenderTargetBitmap(width, height, dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        bitmap.Render(content);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None); encoder.Save(stream);
    }
    private static async Task WaitAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        while (!condition()) await Task.Delay(25, timeout.Token);
    }
    private sealed class MountedEntry : IDesktopTextEntry, IDesktopTextEntryLifetime, IDesktopTextEntryActivitySink
    {
        public MountedEntry(string id, Panel parent, DesktopHostedTextEntryAction action)
        {
            Id = id;
            parent.Children.Add(new TextBlock { Text = id == "composer" ? "Composer" : "Answer field", FontSize = 16, Margin = new Thickness(0, 8, 0, 5) });
            Box = new TextBox { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Height = 100 };
            Button = new Button { Content = action.Label, HorizontalAlignment = HorizontalAlignment.Left, Padding = new Thickness(12, 5, 12, 5), Margin = new Thickness(0, 6, 0, 4) };
            Status = new TextBlock { TextWrapping = TextWrapping.Wrap, MinHeight = 36 };
            parent.Children.Add(Box); parent.Children.Add(Button); parent.Children.Add(Status);
            Button.Click += (_, _) => pendingClick = action.ExecuteAsync(this, CancellationToken.None);
        }
        private Task pendingClick = Task.CompletedTask;
        public TextBox Box { get; }
        public Button Button { get; }
        public TextBlock Status { get; }
        public DesktopTextEntryActivity? LastActivity { get; private set; }
        public CancellationTokenSource Available { get; } = new();
        public string Id { get; }
        public string Purpose => Id;
        public string Text => Box.Text;
        public int SelectionStart => Box.SelectionStart;
        public int SelectionLength => Box.SelectionLength;
        public CancellationToken AvailabilityToken => Available.Token;
        public Task ClickAsync() { Button.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent)); return pendingClick; }
        public Task<DesktopOperationResult> InsertAsync(string text, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested(); Available.Token.ThrowIfCancellationRequested(); Box.Dispatcher.VerifyAccess();
            if (!Box.IsVisible) throw new InvalidOperationException("Acceptance input is not shown.");
            var start = Box.SelectionStart; Box.SelectedText = text; Box.CaretIndex = start + text.Length;
            return Task.FromResult(new DesktopOperationResult(DesktopOperationState.Completed));
        }
        public void ReportActivity(DesktopTextEntryActivity activity) { LastActivity = activity; Status.Text = activity.Message; Button.Content = activity.ButtonLabel ?? Button.Content; }
    }
}
