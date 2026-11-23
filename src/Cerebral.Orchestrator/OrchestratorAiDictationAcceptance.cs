using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Cerebral.Desktop;
using Cerebral.Desktop.Plugins;

namespace Cerebral.Orchestrator;

internal sealed record AiDictationAcceptanceOptions(string ManifestPath, string RuntimePath, string ModelPath, int Seconds, string? Root = null);

/// <summary>
/// Runs the real removable dictation package in the complete Cerebral window while keeping its
/// package and settings outside the user's catalog. The parent owns cleanup after the child exits,
/// because a loaded native plugin assembly may remain mapped for the lifetime of its process.
/// </summary>
internal static class AiDictationAppAcceptance
{
    private const string ParentSwitch = "--accept-ai-dictation-app";
    internal const string ChildSwitch = "--check-ai-dictation-app-live";

    public static bool IsParent(string[] args) => args.Contains(ParentSwitch, StringComparer.Ordinal);
    public static bool IsChild(string[] args) => args.Contains(ChildSwitch, StringComparer.Ordinal);

    public static int RunParent(string[] args)
    {
        var options = Parse(args, ParentSwitch, rootRequired: false);
        var repository = RepositoryRoot();
        var scratch = Path.Combine(repository, "tests", ".scratch");
        var root = Path.Combine(scratch, "ai-dictation-app-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var entryAssembly = Assembly.GetEntryAssembly()?.Location
                ?? throw new InvalidOperationException("The Cerebral entry assembly is unavailable.");
            var appHost = Path.ChangeExtension(entryAssembly, ".exe");
            var start = File.Exists(appHost)
                ? new ProcessStartInfo(appHost)
                : new ProcessStartInfo(Environment.ProcessPath ?? "dotnet") { };
            if (!File.Exists(appHost)) start.ArgumentList.Add(entryAssembly);
            start.UseShellExecute = false;
            start.CreateNoWindow = false;
            start.WindowStyle = ProcessWindowStyle.Normal;
            start.RedirectStandardOutput = true;
            start.RedirectStandardError = true;
            foreach (var argument in new[]
            {
                ChildSwitch, "--root", root, "--manifest", options.ManifestPath,
                "--runtime", options.RuntimePath, "--model", options.ModelPath,
                "--seconds", options.Seconds.ToString(System.Globalization.CultureInfo.InvariantCulture)
            }) start.ArgumentList.Add(argument);

            using var child = Process.Start(start) ?? throw new InvalidOperationException("The owned Cerebral acceptance process did not start.");
            var output = child.StandardOutput.ReadToEndAsync();
            var error = child.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(4));
            try { child.WaitForExitAsync(timeout.Token).GetAwaiter().GetResult(); }
            catch (OperationCanceledException)
            {
                child.Kill(entireProcessTree: true);
                child.WaitForExit();
                throw new TimeoutException("The complete-application AI dictation acceptance exceeded four minutes.");
            }
            var stdout = output.GetAwaiter().GetResult();
            var stderr = error.GetAwaiter().GetResult();
            if (!string.IsNullOrWhiteSpace(stdout)) Console.Write(stdout);
            if (child.ExitCode != 0) throw new InvalidOperationException("Complete-application AI dictation acceptance failed: " + stderr.Trim());
            return 0;
        }
        finally
        {
            var exact = Path.GetFullPath(root);
            if (!exact.StartsWith(Path.GetFullPath(scratch) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                !Path.GetFileName(exact).StartsWith("ai-dictation-app-", StringComparison.Ordinal))
                throw new IOException("Refusing AI dictation acceptance cleanup outside its owned scratch directory.");
            if (Directory.Exists(exact)) Directory.Delete(exact, recursive: true);
        }
    }

    public static int RunChild(Application app, string[] args)
    {
        var options = Parse(args, ChildSwitch, rootRequired: true);
        app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        app.Dispatcher.BeginInvoke(new Action(async () =>
        {
            var exit = 0;
            OrchestratorWindow? window = null;
            try
            {
                var root = Path.GetFullPath(options.Root!);
                var packages = Path.Combine(root, "packages");
                var data = Path.Combine(root, "data");
                var catalog = new DesktopPluginCatalog(packages);
                var installed = catalog.Install(options.ManifestPath);
                if (installed.Id != "pegasus.ai-dictation" || installed.Enabled)
                    throw new InvalidOperationException("The isolated AI dictation package did not begin disabled.");
                catalog.SetEnabled(installed.Id, true);
                WriteSettings(data, options);

                window = new OrchestratorWindow(catalog, data)
                {
                    Width = 1024,
                    Height = 768,
                    WindowStartupLocation = WindowStartupLocation.CenterScreen,
                    Title = "Pegasus Cerebral — AI dictation acceptance"
                };
                var result = await window.AcceptPersonSpokenDictationAsync(options.Seconds);
                Console.WriteLine(JsonSerializer.Serialize(new
                {
                    suite = "ai-dictation-complete-application",
                    evidence = "actual removable package, complete shown Cerebral window, default microphone and local model inference",
                    result.Characters,
                    result.Seconds,
                    insertedInto = "MessageComposer",
                    submitted = false,
                    transcriptPrinted = false,
                    userPluginCatalogChanged = false
                }));
            }
            catch (Exception error) { exit = 1; Console.Error.WriteLine(error); }
            finally
            {
                window?.Close();
                app.Shutdown(exit);
            }
        }), DispatcherPriority.Normal);
        return app.Run();
    }

    public static int CheckCoreWithoutPlugins()
    {
        var root = Path.Combine(Path.GetTempPath(), "cerebral-zero-plugin-ui-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        OrchestratorWindow? window = null;
        try
        {
            window = new OrchestratorWindow(new DesktopPluginCatalog(Path.Combine(root, "packages")), Path.Combine(root, "data"));
            var checks = window.CheckCoreWithoutPlugins();
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                suite = "cerebral-zero-plugin-ui",
                evidence = "actual Cerebral WPF composition with an isolated empty desktop plugin catalog",
                checks,
                optionalPlugins = 0,
                userPluginCatalogChanged = false,
                serviceContacted = false
            }));
            return 0;
        }
        finally
        {
            window?.Close();
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static AiDictationAcceptanceOptions Parse(string[] args, string mode, bool rootRequired)
    {
        var known = new HashSet<string>(StringComparer.Ordinal)
            { mode, "--root", "--manifest", "--runtime", "--model", "--seconds" };
        for (var index = 0; index < args.Length; index++)
        {
            if (!known.Contains(args[index])) throw new ArgumentException("Unknown AI dictation acceptance option: " + args[index]);
            if (args[index] == mode) continue;
            if (++index >= args.Length || args[index].StartsWith("--", StringComparison.Ordinal))
                throw new ArgumentException("AI dictation acceptance option requires a value.");
        }
        string? Option(string name)
        {
            var matches = args.Select((value, index) => (value, index)).Where(x => x.value == name).ToList();
            if (matches.Count > 1) throw new ArgumentException("AI dictation acceptance option was repeated: " + name);
            return matches.Count == 0 ? null : args.ElementAtOrDefault(matches[0].index + 1);
        }
        string RequiredFile(string name)
        {
            var value = Option(name) ?? throw new ArgumentException("AI dictation acceptance requires " + name + ".");
            if (!Path.IsPathFullyQualified(value) || !File.Exists(value))
                throw new FileNotFoundException("AI dictation acceptance requires an existing absolute file for " + name + ".", value);
            return Path.GetFullPath(value);
        }
        var root = Option("--root");
        if (rootRequired && (root is null || !Path.IsPathFullyQualified(root) || !Directory.Exists(root)))
            throw new DirectoryNotFoundException("The child acceptance requires its existing absolute owned root.");
        if (rootRequired)
        {
            var exactRoot = Path.GetFullPath(root!);
            var scratch = Path.GetFullPath(Path.Combine(RepositoryRoot(), "tests", ".scratch"));
            if (!exactRoot.StartsWith(scratch + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                !Path.GetFileName(exactRoot).StartsWith("ai-dictation-app-", StringComparison.Ordinal))
                throw new IOException("The child acceptance root is outside its owned scratch boundary.");
        }
        var secondsText = Option("--seconds");
        var seconds = secondsText is null ? 7 : int.Parse(secondsText, System.Globalization.CultureInfo.InvariantCulture);
        if (seconds is < 3 or > 20) throw new ArgumentOutOfRangeException(nameof(args), "Recording seconds must be between 3 and 20.");
        return new(RequiredFile("--manifest"), RequiredFile("--runtime"), RequiredFile("--model"), seconds,
            root is null ? null : Path.GetFullPath(root));
    }

    private static void WriteSettings(string dataRoot, AiDictationAcceptanceOptions options)
    {
        var directory = Path.Combine(dataRoot, "pegasus.ai-dictation");
        Directory.CreateDirectory(directory);
        var settings = new
        {
            SchemaVersion = 1,
            RuntimePath = options.RuntimePath,
            ModelPath = options.ModelPath,
            ModelId = "nvidia/parakeet-tdt-0.6b-v3",
            ModelRevision = "541d1f99c6b0c3cd0b11a95167540bb8edefd82b",
            ModelSource = "https://huggingface.co/nvidia/parakeet-tdt-0.6b-v3",
            ModelLicense = "CC-BY-4.0",
            RuntimeSource = "https://github.com/NVIDIA/NeMo-Speech.cpp/releases/tag/v0.1.0",
            RuntimeLicense = "Apache-2.0"
        };
        File.WriteAllText(Path.Combine(directory, "ai-dictation.settings.json"),
            JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static string RepositoryRoot() => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../.."));
}

public sealed partial class OrchestratorWindow
{
    internal int CheckCoreWithoutPlugins()
    {
        EnterInspectionMode();
        ReloadDesktopPlugins();
        var checks = 0;
        void Check(bool condition, string name)
        {
            if (!condition) throw new InvalidOperationException("Zero-plugin core check failed: " + name);
            checks++;
        }
        Check(desktopPluginCatalog.List().Count == 0, "catalog is empty");
        Check(desktopPlugins is not null && desktopPlugins.Status.Count == 0, "runtime accepts no packages");
        Check(desktopPlugins!.Panels.Count == 0 && desktopPlugins.ComposerActions.Count == 0 &&
            desktopPlugins.TextEntryActions.Count == 0 && desktopPlugins.InputModes.Count == 0, "contribution surfaces are empty");
        var pageIds = Tabs.Items.OfType<TabItem>().Select(x => x.Tag as string).Where(x => x is not null).ToHashSet(StringComparer.Ordinal);
        Check(pageIds.SetEquals(["chat", "work", "files", "tools"]), "core pages remain composed");
        Check(AutomationProperties.GetAutomationId(prompt) == "MessageComposer" && prompt.IsEnabled, "core composer remains available");
        Check(AutomationProperties.GetAutomationId(conversations) == "ConversationList" && conversations.IsEnabled, "core navigation remains available");
        Check(desktopInputModeBar.Visibility == Visibility.Collapsed, "empty plugin input surface stays absent");
        Check(desktopSidebarTabs.Items.Count == 0 && desktopPluginActions.Children.Count == 0, "empty plugin panel and action surfaces stay absent");
        return checks;
    }

    internal async Task<(int Characters, int Seconds)> AcceptPersonSpokenDictationAsync(int seconds)
    {
        EnterInspectionMode(TimeSpan.FromMinutes(3));
        Show();
        Activate();
        Topmost = true;
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        Topmost = false;
        ReloadDesktopPlugins();
        UpdateLayout();
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);

        var loaded = desktopPlugins?.Status.SingleOrDefault(x => x.Id == "pegasus.ai-dictation");
        if (loaded is not { Loaded: true, Error: null })
            throw new InvalidOperationException("The isolated AI dictation package did not load: " + (loaded?.Error ?? "no status"));
        var button = FindByAutomationId(this, "PluginTextEntryAction-pegasus.ai-dictation-ai-dictate-composer") as Button
            ?? throw new InvalidOperationException("The AI dictation action was not mounted beside the actual Cerebral composer.");
        var activity = FindByAutomationId(this, "TextEntryActivity-composer") as TextBlock
            ?? throw new InvalidOperationException("The composer activity feedback was not mounted.");
        prompt.Clear();
        prompt.Focus();
        sessionHeading.Text = "AI dictation acceptance";
        sessionState.Text = "Starting the local microphone. Speak normally when the countdown appears.";
        button.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        await WaitAsync(() => button.IsEnabled && Equals(button.Content, "Stop AI"), activity, TimeSpan.FromSeconds(10));
        for (var remaining = seconds; remaining > 0; remaining--)
        {
            sessionState.Text = $"Speak now — recording locally for {remaining} more second{(remaining == 1 ? "" : "s")}.";
            await Task.Delay(TimeSpan.FromSeconds(1), Lifetime.Token);
        }
        sessionState.Text = "Transcribing locally. Your words will remain in the composer and will not be submitted.";
        button.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        await WaitAsync(() => prompt.Text.Length > 0 || activity.Foreground == Ui.ErrorBorder,
            activity, TimeSpan.FromMinutes(2));
        if (prompt.Text.Length == 0) throw new InvalidOperationException("AI dictation did not insert text: " + activity.Text);
        await WaitAsync(() => button.IsEnabled && button.Content?.ToString()?.Contains("AI Dictate", StringComparison.Ordinal) == true,
            activity, TimeSpan.FromSeconds(10));
        sessionState.Text = $"AI dictation inserted {prompt.Text.Length} characters into this composer. Nothing was submitted.";
        return (prompt.Text.Length, seconds);
    }

    private async Task WaitAsync(Func<bool> condition, TextBlock activity, TimeSpan timeout)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(Lifetime.Token);
        deadline.CancelAfter(timeout);
        while (!condition())
        {
            if (activity.Foreground == Ui.ErrorBorder) throw new InvalidOperationException(activity.Text);
            await Task.Delay(50, deadline.Token);
        }
    }

    private static FrameworkElement? FindByAutomationId(DependencyObject root, string id)
    {
        if (root is FrameworkElement element && AutomationProperties.GetAutomationId(element) == id) return element;
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
            if (FindByAutomationId(VisualTreeHelper.GetChild(root, index), id) is { } found) return found;
        return null;
    }
}
