using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using Cerebral.Client;
using Cerebral.Core;
using Cerebral.Desktop;

namespace Cerebral.Orchestrator;

internal sealed record SourceLocalOptions(string DataDirectory, string? ModelPath, string? LlamaServerPath)
{
    public static SourceLocalOptions? FromArguments(string[] args)
    {
        var sourceIndex = Array.IndexOf(args, "--source-local");
        var defaultData = DesktopInstancePaths.DefaultSourceDataDirectory;
        // Only an explicit argument or complete inherited isolated binding selects a development dataset.
        // Binary layout alone cannot silently switch away from the owner's selected durable roots.
        if (sourceIndex < 0)
            return DesktopInstancePaths.ReadInheritedSource() is { } inherited
                ? new(NormalizeDirectory(inherited.DataDirectory, "Source data"), null, null) : null;
        if (args.Count(value => value == "--source-local") != 1)
            throw new ArgumentException("Use --source-local once.");

        var data = ValueAfter(args, sourceIndex) ?? defaultData;
        var model = OptionalValue(args, "--model");
        var runtime = OptionalValue(args, "--llama-server");
        return new(NormalizeDirectory(data, "Source data"), NormalizeFile(model, "Model"),
            NormalizeFile(runtime, "llama.cpp runtime"));
    }

    private static string? OptionalValue(string[] args, string name)
    {
        var indexes = args.Select((value, index) => (value, index)).Where(item => item.value == name).Select(item => item.index).ToArray();
        if (indexes.Length == 0) return null;
        if (indexes.Length != 1 || ValueAfter(args, indexes[0]) is not { } value)
            throw new ArgumentException($"{name} requires one absolute path.");
        return value;
    }

    private static string? ValueAfter(string[] args, int index) =>
        index + 1 < args.Length && !args[index + 1].StartsWith("--", StringComparison.Ordinal) ? args[index + 1] : null;

    private static string NormalizeDirectory(string path, string label)
    {
        if (!Path.IsPathFullyQualified(path) || path.Contains('\0')) throw new ArgumentException($"{label} requires an absolute path.");
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    }

    private static string? NormalizeFile(string? path, string label)
    {
        if (path is null) return null;
        if (!Path.IsPathFullyQualified(path) || path.Contains('\0')) throw new ArgumentException($"{label} requires an absolute path.");
        return Path.GetFullPath(path);
    }
}

internal sealed record SourceLocalReady(LocalWorkerConnection Connection, string ModelId, string ModelName, int Profiles);

internal static class SourceLocalStartup
{
    public static async Task<SourceLocalReady> PrepareAsync(SourceLocalOptions options, Action<string> progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(progress);

        if (options.ModelPath is not null && !File.Exists(options.ModelPath))
            throw new FileNotFoundException("The selected local GGUF model is unavailable.", options.ModelPath);
        if (options.LlamaServerPath is not null && !File.Exists(options.LlamaServerPath))
            throw new FileNotFoundException("The selected llama.cpp runtime is unavailable.", options.LlamaServerPath);

        var instancePaths = DesktopInstancePaths.Current;
        if (!instancePaths.SourceLocal || !PathEquals(instancePaths.DataDirectory, options.DataDirectory))
            throw new InvalidOperationException("Source startup requires the selected source desktop storage binding.");
        ConnectionStore.UseProcessLocalDataDirectory(instancePaths.ConnectionDataDirectory);
        progress("Worker · Starting the current source service…");
        var connection = await LocalDevelopment.StartPipeAsync(cancellationToken, options.DataDirectory,
            typeof(Cerebral.Desktop.Plugins.IDesktopPlugin).Assembly.Location).ConfigureAwait(true);
        SaveConnection(connection);

        using var client = connection.ToPreferences().CreateClient();
        progress("Workspace · Preparing isolated data and development tools…");
        var settings = await client.GetAsync<HostSettings>("settings.get", cancellationToken: cancellationToken).ConfigureAwait(true);
        var settingsChanged = false;
        if (options.LlamaServerPath is not null && !PathEquals(settings.LlamaServerPath, options.LlamaServerPath))
        {
            settings.LlamaServerPath = options.LlamaServerPath;
            settingsChanged = true;
        }
        // Source-local mode is an explicit development launch. It enables configured development
        // commands in this isolated store; it does not enable Cortex root access or alter installation policy.
        if (!settings.ExternalCommandsEnabled) { settings.ExternalCommandsEnabled = true; settingsChanged = true; }
        if (settingsChanged) await RequireAsync(client, "settings.save", settings, cancellationToken).ConfigureAwait(true);

        var models = await client.GetAsync<List<ModelRecord>>("models.list", cancellationToken: cancellationToken).ConfigureAwait(true);
        ModelRecord? requested = null;
        if (options.ModelPath is not null)
        {
            requested = models.FirstOrDefault(model => PathEquals(model.Path, options.ModelPath));
            if (requested is null)
            {
                progress("Models · Registering the existing local model…");
                var id = AvailableModelId(models, options.ModelPath);
                var result = await RequireAsync(client, "models.import", new
                {
                    id,
                    name = Path.GetFileNameWithoutExtension(options.ModelPath),
                    path = options.ModelPath,
                    copy = false,
                    automaticContextSizing = true,
                    parallelRequests = 1,
                    gpuLayers = -1,
                    idleMinutes = -1,
                    starterPackage = true,
                    packagePurpose = "Source-local development"
                }, cancellationToken).ConfigureAwait(true);
                requested = CerebralJson.Read<ModelRecord>(result.Data);
                models.Add(requested);
            }
        }

        var profiles = await client.GetAsync<List<AgentProfile>>("agents.list", cancellationToken: cancellationToken).ConfigureAwait(true);
        var leads = profiles.Where(profile => profile.Role == "lead").ToList();
        if (leads.Count > 1) throw new InvalidOperationException("The source store contains more than one lead Cerebral profile. Open Configuration and select one lead before continuing.");
        var selectedWorker = SelectedModelWorker(leads);
        ModelRecord selected;
        if (selectedWorker == "local")
        {
            selected = leads.Count == 1
                ? models.FirstOrDefault(model => model.Id == leads[0].ModelId)
                    ?? throw new InvalidOperationException("The configured source lead references a missing local model.")
                : requested
                    ?? models.FirstOrDefault(model => model.Id == settings.DefaultModelId)
                    ?? models.FirstOrDefault()
                    ?? throw new InvalidOperationException("No source model is configured. Launch again with --model followed by an existing GGUF path.");

            var pause = await client.GetAsync<ResourcePauseStatus>("resources.local.status",
                cancellationToken: cancellationToken).ConfigureAwait(true);
            if (ShouldLoadLocalModel(pause.State))
            {
                if (string.IsNullOrWhiteSpace(settings.LlamaServerPath) || !File.Exists(settings.LlamaServerPath))
                    throw new FileNotFoundException("No usable llama.cpp runtime is configured. Launch with --llama-server followed by the existing llama-server executable.", settings.LlamaServerPath);
                progress($"Models · Loading {selected.Name}…");
                var loaded = await RequireAsync(client, "models.load", new { id = selected.Id }, cancellationToken).ConfigureAwait(true);
                selected = CerebralJson.Read<ModelRecord>(loaded.Data);
            }
            else progress($"Models · Local GPU remains {pause.State.ToString().ToLowerInvariant()}. {pause.Detail}");
        }
        else
        {
            // Model paths and runtime requirements belong to the selected worker, even when
            // this coordinator has a different local registration with the same model ID.
            if (leads.Count != 1)
                throw new InvalidOperationException("Set up the source Cerebral lead before selecting a remote model.");
            progress($"Models · Connecting to {selectedWorker}…");
            selected = await client.GetAsync<ModelRecord>("workers.models.inspect",
                new { workerId = selectedWorker, id = leads[0].ModelId }, cancellationToken: cancellationToken).ConfigureAwait(true);
            if (selected.State != "loaded")
            {
                progress($"Models · Loading {selected.Name} on {selectedWorker}…");
                var loaded = await RequireAsync(client, "workers.models.load",
                    new { workerId = selectedWorker, id = selected.Id }, cancellationToken).ConfigureAwait(true);
                selected = CerebralJson.Read<ModelRecord>(loaded.Data);
            }
        }

        if (leads.Count == 0)
        {
            progress("Agents · Creating the default Cerebral team…");
            await RequireAsync(client, "agents.bootstrap", new { modelId = selected.Id }, cancellationToken).ConfigureAwait(true);
            profiles = await client.GetAsync<List<AgentProfile>>("agents.list", cancellationToken: cancellationToken).ConfigureAwait(true);
        }
        progress("Plugins · Preparing desktop extensions…");
        return new(connection, selected.Id, selected.Name, profiles.Count);
    }

    internal static string SelectedModelWorker(IReadOnlyList<AgentProfile> leads) =>
        leads.Count == 1 ? leads[0].ModelWorkerId : "local";

    internal static bool ShouldLoadLocalModel(LocalResourceState state) => state == LocalResourceState.Active;

    private static void SaveConnection(LocalWorkerConnection connection)
    {
        var current = ConnectionStore.Read();
        var desired = connection.ToPreferences(autoStart: true);
        if (current.Preferences is { Transport: "pipe" } saved && saved.LocalEndpoint == desired.LocalEndpoint &&
            PathEquals(saved.LocalDataDirectory, desired.LocalDataDirectory) && saved.AutoStartLocalServices)
            return;
        _ = ConnectionStore.SaveFromIntegrator(desired, current.Revision);
    }

    private static async Task<CommandResult> RequireAsync(CerebralClient client, string operation, object arguments,
        CancellationToken cancellationToken)
    {
        var result = await client.CommandAsync(operation, arguments, cancellationToken: cancellationToken).ConfigureAwait(true);
        if (!result.Success) throw new InvalidOperationException(result.Error ?? $"{operation} failed.");
        return result;
    }

    private static string AvailableModelId(IReadOnlyCollection<ModelRecord> models, string path)
    {
        var stem = new string(Path.GetFileNameWithoutExtension(path).ToLowerInvariant()
            .Select(character => char.IsAsciiLetterOrDigit(character) ? character : '-').ToArray()).Trim('-');
        if (stem.Length == 0) stem = "model";
        if (stem.Length > 70) stem = stem[..70].TrimEnd('-');
        var basis = "source-" + stem;
        var ids = models.Select(model => model.Id).ToHashSet(StringComparer.Ordinal);
        if (!ids.Contains(basis)) return basis;
        for (var suffix = 2; suffix < 10_000; suffix++)
            if (!ids.Contains(basis + "-" + suffix)) return basis + "-" + suffix;
        throw new InvalidOperationException("Could not allocate a source model identifier.");
    }

    private static bool PathEquals(string? left, string? right) => !string.IsNullOrWhiteSpace(left) && !string.IsNullOrWhiteSpace(right) &&
        Path.GetFullPath(left).Equals(Path.GetFullPath(right), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}

internal sealed class MainStartupException(string stage, Exception cause, bool retryAllowed = true,
    MainStartupPluginRecovery? pluginRecovery = null)
    : InvalidOperationException("Startup stopped during " + stage + ": " + cause.Message, cause)
{
    internal string Stage { get; } = stage;
    internal bool RetryAllowed { get; } = retryAllowed;
    internal MainStartupPluginRecovery? PluginRecovery { get; } = pluginRecovery;
}

internal sealed class CerebralStartupWindow : Window
{
    private readonly TextBlock status;
    private readonly ProgressBar progress = new() { IsIndeterminate = true, Height = 6, Margin = new Thickness(0, 8, 0, 18) };
    private readonly Button close;
    private readonly Button openConfiguration;
    private readonly Button retry;
    private readonly Button disableOptionalPlugin;
    private readonly StackPanel recoveryPanel = new() { Visibility = Visibility.Collapsed, Margin = new Thickness(0, 10, 0, 0) };
    private readonly TextBlock recoveryExplanation = Ui.Text("", 12, Ui.Muted);
    private readonly Expander diagnosticExpander;
    private readonly bool allowOrdinaryConfiguration;
    private readonly TextBox diagnostics = Ui.Json();
    private readonly Queue<string> diagnosticLines = new();
    private readonly TaskCompletionSource firstContentRendered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Func<Task>? retryAction;
    private MainStartupPluginRecovery? pluginRecovery;
    private bool failureShown, retryInProgress;
    private string currentStage = "Startup";

    public CerebralStartupWindow(string description, string initialStatus, string buildDetails,
        string title = "Pegasus Cerebral", bool allowOrdinaryConfiguration = true)
    {
        if (string.IsNullOrWhiteSpace(description)) throw new ArgumentException("A startup description is required.", nameof(description));
        if (string.IsNullOrWhiteSpace(initialStatus)) throw new ArgumentException("An initial startup status is required.", nameof(initialStatus));
        if (string.IsNullOrWhiteSpace(buildDetails)) throw new ArgumentException("Startup build details are required.", nameof(buildDetails));
        this.allowOrdinaryConfiguration = allowOrdinaryConfiguration;
        Title = title;
        Icon = Branding.IconFor("Orchestrator");
        Width = 760;
        Height = 470;
        MinWidth = 760;
        MinHeight = 470;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        var layout = new Grid();
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(270) });
        layout.ColumnDefinitions.Add(new ColumnDefinition());
        var artwork = new Image
        {
            Source = Branding.SplashArtwork(),
            Stretch = Stretch.UniformToFill,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
        };
        RenderOptions.SetBitmapScalingMode(artwork, BitmapScalingMode.HighQuality);
        AutomationProperties.SetName(artwork, "Pegasus Cerebral loading artwork");
        layout.Children.Add(artwork);
        var body = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        body.Children.Add(Ui.Text("Pegasus Cerebral", 24));
        body.Children.Add(Ui.Text(description, 13, Ui.Muted));
        status = Ui.Text(initialStatus, 14, Ui.Muted);
        status.TextWrapping = TextWrapping.Wrap;
        body.Children.Add(status);
        body.Children.Add(progress);
        var build = Ui.Text(buildDetails, 11, Ui.Muted);
        build.TextTrimming = System.Windows.TextTrimming.CharacterEllipsis;
        build.ToolTip = buildDetails;
        body.Children.Add(build);
        recoveryExplanation.TextWrapping = TextWrapping.Wrap;
        recoveryPanel.Children.Add(recoveryExplanation);
        disableOptionalPlugin = Ui.Button("Disable optional plugin and continue", async () => await DisableOptionalPluginAsync());
        AutomationProperties.SetAutomationId(disableOptionalPlugin, "StartupDisableOptionalPlugin");
        recoveryPanel.Children.Add(Ui.Bar(disableOptionalPlugin));
        body.Children.Add(recoveryPanel);
        diagnostics.IsReadOnly = true;
        diagnostics.MinHeight = 100;
        diagnostics.MaxHeight = 150;
        diagnostics.TextWrapping = TextWrapping.Wrap;
        diagnostics.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        diagnosticExpander = new Expander
        {
            Header = "Startup details",
            Content = diagnostics,
            IsExpanded = false,
            Margin = new Thickness(0, 12, 0, 0)
        };
        AutomationProperties.SetAutomationId(diagnosticExpander, "StartupDiagnostics");
        body.Children.Add(diagnosticExpander);
        openConfiguration = Ui.Button("Open Configuration", OpenConfiguration);
        openConfiguration.Visibility = Visibility.Collapsed;
        close = Ui.Button("Close", Close);
        close.Visibility = Visibility.Collapsed;
        retry = Ui.Button("Retry", async () => await RetryAsync());
        retry.Visibility = Visibility.Collapsed;
        AutomationProperties.SetAutomationId(retry, "StartupRetry");
        var actions = Ui.Bar(openConfiguration, retry, Ui.Button("Copy details", CopyDiagnostics), close);
        actions.Margin = new Thickness(0, 10, 0, 0);
        // Keep recovery actions reachable even with long diagnostics or larger text.
        var right = new Grid { Margin = new Thickness(32) };
        right.RowDefinitions.Add(new RowDefinition());
        right.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        right.Children.Add(new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalContentAlignment = VerticalAlignment.Center });
        Grid.SetRow(actions, 1); right.Children.Add(actions);
        Grid.SetColumn(right, 1); layout.Children.Add(right);
        Content = layout;
        ContentRendered += OnFirstContentRendered;
        Closed += OnStartupClosed;
        WindowSizing.Attach(this);
        AppendDiagnostic("Startup", initialStatus);
    }

    public static CerebralStartupWindow ForInstalled() => new(
        "Starting your private local AI workspace.",
        "Interface · Loading conversations and desktop extensions…",
        $"Build {ApplicationVersion.Current} · {System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture}");

    public static CerebralStartupWindow ForAttached() => new(
        "Connecting to the selected local Cerebral workspace.",
        "Interface · Preparing the attached conversation workspace…",
        $"Build {ApplicationVersion.Current} · {System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture}",
        allowOrdinaryConfiguration: false);

    public static CerebralStartupWindow ForSource(SourceLocalOptions options) => new(
        "Starting an isolated source workspace. The installed deployment remains unchanged.",
        "Preparing the current source build…",
        $"Source build {ApplicationVersion.Current} · {System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture}\n{options.DataDirectory}",
        "Pegasus Cerebral · Source");

    public string DiagnosticText => diagnostics.Text;
    public string CurrentStatus => status.Text;
    internal bool OrdinaryConfigurationActionVisible => openConfiguration.Visibility == Visibility.Visible;
    internal bool RetryActionVisible => retry.Visibility == Visibility.Visible;
    internal bool PluginRecoveryActionVisible => recoveryPanel.Visibility == Visibility.Visible;

    // Show and dispatcher idle do not establish that WPF rendered the loading content.
    // The first event admits Main construction; a retry reuses this same rendered splash.
    internal async Task WaitForFirstContentRenderedAsync(CancellationToken cancellationToken)
    {
        Dispatcher.VerifyAccess();
        cancellationToken.ThrowIfCancellationRequested();
        await firstContentRendered.Task.WaitAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsLoaded || !IsVisible)
            throw new MainStartupException("Loading screen", new InvalidOperationException("The loading screen is no longer available."));
    }

    private void OnFirstContentRendered(object? sender, EventArgs args)
    {
        ContentRendered -= OnFirstContentRendered;
        firstContentRendered.TrySetResult();
    }

    private void OnStartupClosed(object? sender, EventArgs args)
    {
        ContentRendered -= OnFirstContentRendered;
        Closed -= OnStartupClosed;
        firstContentRendered.TrySetCanceled();
    }

    internal void ConfigureRetry(Func<Task> action) => retryAction = action ?? throw new ArgumentNullException(nameof(action));

    internal void BeginAttempt()
    {
        failureShown = false;
        pluginRecovery = null; recoveryPanel.Visibility = Visibility.Collapsed;
        openConfiguration.Visibility = retry.Visibility = Visibility.Collapsed;
        close.Content = "Cancel"; close.Visibility = Visibility.Visible;
        progress.IsIndeterminate = true;
        AppendDiagnostic("Attempt", "Starting a fresh hidden Main and plugin generation.");
    }

    private async Task RetryAsync()
    {
        if (!failureShown || retryInProgress || retryAction is null) return;
        retryInProgress = true; retry.IsEnabled = disableOptionalPlugin.IsEnabled = false;
        try { await retryAction(); }
        catch (Exception error) { ShowFailure(error); }
        finally { retryInProgress = false; retry.IsEnabled = disableOptionalPlugin.IsEnabled = true; }
    }

    internal async Task DisableOptionalPluginAsync()
    {
        if (!failureShown || retryInProgress || retryAction is null || pluginRecovery is not { Consumed: false } selected) return;
        retryInProgress = true; retry.IsEnabled = disableOptionalPlugin.IsEnabled = false;
        pluginRecovery = null; recoveryPanel.Visibility = Visibility.Collapsed;
        try
        {
            selected.Disable();
            AppendDiagnostic("Recovery", selected.PluginName + " was disabled explicitly. Saved preferences were preserved.");
            await retryAction();
        }
        catch (Exception error) when (error is not OutOfMemoryException) { ShowFailure(error); }
        finally { retryInProgress = false; retry.IsEnabled = disableOptionalPlugin.IsEnabled = true; }
    }

    public void SetProgress(string message)
    {
        if (failureShown) { AppendDiagnostic("Later progress", message); return; }
        currentStage = message.Split('·', 2)[0].Trim();
        status.Text = SafeMessage(message);
        status.Foreground = Ui.Muted;
        AppendDiagnostic("Progress", message);
        FlushProgressFrame();
    }

    public void SetWarning(string message)
    {
        if (failureShown) { AppendDiagnostic("Additional attention", message); return; }
        status.Text = SafeMessage(message);
        status.Foreground = Ui.ErrorText;
        AppendDiagnostic("Attention", message);
        FlushProgressFrame();
    }

    public void ShowFailure(Exception error)
    {
        failureShown = true;
        progress.IsIndeterminate = false;
        progress.Value = 0;
        var stage = error is MainStartupException failure ? failure.Stage : currentStage;
        status.Text = SafeMessage(stage + " · Startup stopped: " + (error.InnerException ?? error).Message);
        status.Foreground = Ui.ErrorText;
        AppendDiagnostic("Failed stage", stage);
        var depth = 0;
        for (var cause = error; cause is not null && depth++ < 5; cause = cause.InnerException)
        {
            AppendDiagnostic(cause.GetType().FullName ?? cause.GetType().Name, cause.Message);
            if (cause.Data["MainStartupCleanup"] is Exception cleanup)
                AppendDiagnostic("Cleanup · " + cleanup.GetType().Name, cleanup.Message);
            if (cause.Data["MainStartupRecovery"] is Exception observation)
                AppendDiagnostic("Recovery unavailable · " + observation.GetType().Name, observation.Message);
        }
        diagnosticExpander.IsExpanded = true;
        openConfiguration.Visibility = allowOrdinaryConfiguration ? Visibility.Visible : Visibility.Collapsed;
        retry.Visibility = retryAction is not null && error is not MainStartupException { RetryAllowed: false }
            ? Visibility.Visible : Visibility.Collapsed;
        pluginRecovery = allowOrdinaryConfiguration && retryAction is not null &&
            error is MainStartupException { RetryAllowed: true, PluginRecovery: { Consumed: false } recovery } ? recovery : null;
        recoveryPanel.Visibility = pluginRecovery is null ? Visibility.Collapsed : Visibility.Visible;
        if (pluginRecovery is not null)
        {
            status.Text = SafeMessage(pluginRecovery.PluginName + " · Startup needs your choice.");
            recoveryExplanation.Text = SafeMessage(pluginRecovery.Explanation);
            disableOptionalPlugin.Content = "Disable " + pluginRecovery.PluginName + " and continue";
        }
        close.Content = "Close"; close.Visibility = Visibility.Visible;
        FlushProgressFrame();
    }

    private void FlushProgressFrame()
    {
        if (!IsLoaded || !IsVisible || Dispatcher.HasShutdownStarted) return;
        UpdateLayout();
        Dispatcher.Invoke(System.Windows.Threading.DispatcherPriority.Render,
            new Action(static () => { }));
    }

    private void AppendDiagnostic(string stage, string message)
    {
        var value = string.IsNullOrWhiteSpace(message) ? "No additional details." : SafeMessage(message);
        if (value.Length > 600) value = value[..600] + "…";
        diagnosticLines.Enqueue($"{DateTime.Now:HH:mm:ss} · {stage} · {value}");
        while (diagnosticLines.Count > 24) diagnosticLines.Dequeue();
        diagnostics.Text = string.Join(Environment.NewLine, diagnosticLines);
        diagnostics.ScrollToEnd();
    }

    private void CopyDiagnostics()
    {
        try { Clipboard.SetText(diagnostics.Text); }
        catch (Exception error) { SetWarning("Startup details could not be copied: " + error.Message); }
    }

    private void OpenConfiguration()
    {
        try { ManagementProcess.OpenApplication("Integrator"); }
        catch (Exception error) { SetWarning("Configuration could not be opened: " + error.Message); }
    }

    internal static string SafeMessage(string message)
    {
        var value = message.Replace('\0', ' ').Trim();
        if (value.Length > 8192) value = value[..8192] + "…";
        value = System.Text.RegularExpressions.Regex.Replace(value,
            """(?i)(["']?(?:access[_-]?key|api[_-]?key|token|password|secret)["']?\s*[:=]\s*)(?:"[^"]*"|'[^']*'|[^\s,;]+)""", "$1[redacted]");
        value = System.Text.RegularExpressions.Regex.Replace(value, @"(?i)(bearer\s+)\S+", "$1[redacted]");
        value = System.Text.RegularExpressions.Regex.Replace(value, @"(?i)\bhttps?://[^\s]+", match =>
        {
            if (!Uri.TryCreate(match.Value, UriKind.Absolute, out var uri)) return "[address omitted]";
            return new UriBuilder(uri) { UserName = "", Password = "", Query = "", Fragment = "" }.Uri.GetLeftPart(UriPartial.Path);
        });
        return value.Length > 1200 ? value[..1200] + "…" : value;
    }
}

internal static class SourceLocalStartupChecks
{
    public static int Verify()
    {
        var root = Path.Combine(Path.GetTempPath(), "cerebral-source-options");
        var model = Path.Combine(root, "model.gguf");
        var runtime = Path.Combine(root, "llama-server.exe");
        var value = SourceLocalOptions.FromArguments(["--source-local", root, "--model", model, "--llama-server", runtime]);
        var passed = 0;
        void Check(bool condition) { if (!condition) throw new InvalidOperationException("Source-local option verification failed."); passed++; }
        Check(value is not null && value.DataDirectory == Path.GetFullPath(root));
        Check(value!.ModelPath == Path.GetFullPath(model) && value.LlamaServerPath == Path.GetFullPath(runtime));
        var implicitSource = SourceLocalOptions.FromArguments([]);
        Check(DesktopInstancePaths.ReadInheritedSource() is null ? implicitSource is null :
            implicitSource?.DataDirectory == DesktopInstancePaths.ReadInheritedSource()?.DataDirectory);
        var defaulted = SourceLocalOptions.FromArguments(["--source-local", "--tray"]);
        Check(defaulted is not null && Path.IsPathFullyQualified(defaulted.DataDirectory));
        var duplicateDenied = false;
        try { _ = SourceLocalOptions.FromArguments(["--source-local", root, "--source-local", root]); }
        catch (ArgumentException) { duplicateDenied = true; }
        Check(duplicateDenied);
        var connectionRoot = Path.Combine(root, "desktop");
        var selected = ConnectionStore.UseProcessLocalDataDirectory(connectionRoot);
        Check(selected == Path.Combine(Path.GetFullPath(connectionRoot), "connection.json") && ConnectionStore.FilePath == selected);
        var changedDenied = false;
        try { _ = ConnectionStore.UseProcessLocalDataDirectory(Path.Combine(root, "other")); }
        catch (InvalidOperationException) { changedDenied = true; }
        Check(changedDenied);
        Check(SourceLocalStartup.ShouldLoadLocalModel(LocalResourceState.Active));
        foreach (var state in new[] { LocalResourceState.Paused, LocalResourceState.Draining,
            LocalResourceState.Resuming, LocalResourceState.Faulted, (LocalResourceState)999 })
            Check(!SourceLocalStartup.ShouldLoadLocalModel(state));
        Check(SourceLocalStartup.SelectedModelWorker([]) == "local");
        Check(SourceLocalStartup.SelectedModelWorker(
            [new AgentProfile { ModelWorkerId = "gb10" }]) == "gb10");
        Check(SourceLocalStartup.SelectedModelWorker(
            [new AgentProfile { ModelWorkerId = "local" }]) == "local");
        return passed;
    }
}
