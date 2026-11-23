using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Cerebral.Desktop.Plugins;

namespace Cerebral.ResourcePausePlugin.Tests;

public static partial class Program
{
    private static int checks;
    private static readonly string TestDataRoot = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "Pegasus-Cerebral-ResourcePause-" + Guid.NewGuid().ToString("N"));

    [STAThread]
    public static int Main(string[] args)
    {
        try
        {
            // Keep offscreen fixture controls on the CPU while the human owns the GPU.
            RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
            _ = new Application();
            Application.Current.Resources[DesktopThemeResourceKeys.TextBrush] = Brushes.Black;
            Application.Current.Resources[DesktopThemeResourceKeys.MutedTextBrush] = Brushes.Gray;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
            if (args is ["--mounted-status-only"])
            {
                InitialStatusMustBeConfirmed();
                PrimaryStatusMountsRemainExactlyThree();
                MountedEngineStatusConvergesAndStops();
                StaleEngineRefreshCannotOverwriteMutation();
                RunPrimaryStatusChecks();
                Console.WriteLine($"Resource Pause mounted status: {checks} deterministic software-WPF checks passed; fixture service only, no live app, model, network, GPU rendering or deployment.");
                return 0;
            }
            if (args is ["--primary-status-only"])
            {
                InitialStatusMustBeConfirmed();
                PrimaryStatusMountsRemainExactlyThree();
                RunPrimaryStatusChecks();
                Console.WriteLine($"Resource Pause primary status: {checks} deterministic software-WPF checks passed; fixture service only, no live app, model, network, GPU rendering or deployment.");
                return 0;
            }
            if (args.Length != 0) throw new ArgumentException("Use --primary-status-only, --mounted-status-only or no arguments.");
            InitialStatusMustBeConfirmed();
            ConfirmedStatesAndAccessibility();
            PauseRequestAndStaleRefresh();
            ResumeRequest();
            RefreshFailureClearsReadiness();
            PartialStatusCannotImplyReady();
            TransitionFailureClearsReadiness();
            RunSettingsProtocolChecks();
            RunAdvancedChecks();
            RunRoutingChecks();
            Console.WriteLine($"Resource Pause presentation: {checks} deterministic WPF checks passed; fixture service only, no live app, model, network or deployment.");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 1;
        }
        finally
        {
            var target = System.IO.Path.GetFullPath(TestDataRoot);
            var parent = System.IO.Path.GetFullPath(System.IO.Path.GetTempPath()).TrimEnd(System.IO.Path.DirectorySeparatorChar);
            if (!string.Equals(System.IO.Path.GetDirectoryName(target), parent, StringComparison.OrdinalIgnoreCase) ||
                !System.IO.Path.GetFileName(target).StartsWith("Pegasus-Cerebral-ResourcePause-", StringComparison.Ordinal))
                throw new IOException("Unsafe resource-pause fixture cleanup target.");
            if (Directory.Exists(target)) Directory.Delete(target, true);
        }
    }

    private static void InitialStatusMustBeConfirmed()
    {
        var reply = new TaskCompletionSource<DesktopServiceCommandResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var host = new FixtureHost { Reply = (operation, _, _) => operation == "settings.get" ? Task.FromResult(LocalDefaults()) : reply.Task };
        using var plugin = Create(host);
        var button = StatusButton(plugin);
        CheckPresentation(button, Brushes.Gray, "Local GPU checking…", "Unknown");
        Check(host.Operations.SequenceEqual(["resources.local.status"]), "initial view reads only status");
        reply.SetResult(Status("active"));
        PumpUntil(() => Label(button) == "Local GPU active");
        CheckPresentation(button, Brushes.LimeGreen, "Local GPU active", "Ready");
        Check(AutomationProperties.GetLiveSetting(button) == AutomationLiveSetting.Polite,
            "readiness changes expose a polite accessibility status");
    }

    private static void ConfirmedStatesAndAccessibility()
    {
        foreach (var sample in new[]
        {
            (State: "active", Active: 2, Queued: 0, Brush: Brushes.LimeGreen, Label: "Local GPU active", Meaning: "Ready", Enabled: true),
            (State: "paused", Active: 0, Queued: 7, Brush: Brushes.LimeGreen, Label: "Local GPU inactive", Meaning: "Ready", Enabled: true),
            (State: "draining", Active: 1, Queued: 3, Brush: Brushes.Gold, Label: "Local GPU draining…", Meaning: "Pending", Enabled: false),
            (State: "resuming", Active: 0, Queued: 3, Brush: Brushes.Gold, Label: "Local GPU reactivating…", Meaning: "Pending", Enabled: false),
            (State: "faulted", Active: 0, Queued: 3, Brush: Brushes.IndianRed, Label: "Local GPU attention", Meaning: "Needs attention", Enabled: true),
            (State: "paused", Active: 1, Queued: 3, Brush: Brushes.IndianRed, Label: "Local GPU attention", Meaning: "Needs attention", Enabled: true),
            (State: "unrecognized", Active: 0, Queued: 0, Brush: Brushes.Gray, Label: "Local GPU unknown", Meaning: "Unknown", Enabled: true)
        })
        {
            var host = new FixtureHost { Reply = (operation, _, _) => Task.FromResult(operation == "settings.get" ? LocalDefaults() : Status(sample.State, sample.Active, sample.Queued)) };
            using var plugin = Create(host);
            var button = StatusButton(plugin);
            CheckPresentation(button, sample.Brush, sample.Label, sample.Meaning);
            Check(button.IsEnabled == sample.Enabled, sample.State + " toggle enablement matches confirmed state");
            Check(AutomationProperties.GetHelpText(button).Contains("fixture detail") &&
                  button.ToolTip?.ToString() == AutomationProperties.GetHelpText(button),
                sample.State + " retains service detail in tooltip and accessible help");
            Check(host.Operations.SequenceEqual(sample.State == "active" ? ["resources.local.status", "settings.get"] : ["resources.local.status"]),
                sample.State + " rendering makes only the required read-only resource and active-route observations");
            if (sample.State == "paused" && sample.Active == 0)
                Check(button.ToolTip!.ToString()!.Contains("queued 7"),
                    "held durable work does not prevent confirmed inactive readiness");

            button.Resources[DesktopThemeResourceKeys.TextBrush] = Brushes.White;
            Check(Indicator(button).Stroke == Brushes.White && LabelView(button).Foreground == Brushes.White,
                sample.State + " text and indicator outline follow the host contrast resource");
        }
    }

    private static void PauseRequestAndStaleRefresh()
    {
        var reply = new TaskCompletionSource<DesktopServiceCommandResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var host = new FixtureHost
        {
            Reply = (operation, _, _) => operation == "resources.local.pause" ? reply.Task : Task.FromResult(operation == "settings.get" ? LocalDefaults() : Status("active"))
        };
        using var plugin = Create(host);
        var button = StatusButton(plugin);
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        CheckPresentation(button, Brushes.Gold, "Local GPU draining…", "Pending");
        Check(!button.IsEnabled && host.PauseReroute, "pause request shows pending immediately and retains approved reroute policy");
        Menu(plugin, "refresh-status").ExecuteAsync(default).GetAwaiter().GetResult();
        CheckPresentation(button, Brushes.Gold, "Local GPU draining…", "Pending");
        Check(host.Operations.Count(operation => operation == "resources.local.pause") == 1,
            "an older active status cannot mark the in-flight owner request ready or replay it");
        reply.SetResult(Status("paused", queued: 2));
        PumpUntil(() => Label(button) == "Local GPU inactive");
        CheckPresentation(button, Brushes.LimeGreen, "Local GPU inactive", "Ready");
    }

    private static void ResumeRequest()
    {
        var resource = "paused"; var defaultModel = "remote"; var defaultWorker = "gb10";
        var host = new FixtureHost
        {
            Reply = (operation, arguments, _) => Task.FromResult(operation switch
            {
                "resources.local.status" => Status(resource),
                "settings.get" => new DesktopServiceCommandResult(true, JsonSerializer.SerializeToElement(new { defaultModelId = defaultModel, defaultModelWorkerId = defaultWorker })),
                "models.list" => new DesktopServiceCommandResult(true, JsonSerializer.SerializeToElement(new[]
                    { new { id = "local", name = "Local", state = "unloaded", error = (string?)null, capabilityStatus = new[] { new { id = "text-generation", usable = true } } } })),
                "engine.status" => Json(new { paused = true, epoch = 3, activeEvaluations = 0, candidatePending = false,
                    active = new { slot = "A", id = "engine-a" }, error = (string?)null,
                    slots = new[] { new { slot = "A", available = true, buildAllowed = false, package = new { slot = "A", id = "engine-a" }, error = (string?)null },
                        new { slot = "B", available = true, buildAllowed = true, package = new { slot = "B", id = "engine-b" }, error = (string?)null } } }),
                "resources.local.resume" => Status(resource = "active"),
                "models.default.switch" => Confirm(arguments),
                _ => throw new InvalidOperationException(operation)
            })
        };
        DesktopServiceCommandResult Confirm(object arguments)
        {
            var value = JsonSerializer.SerializeToElement(arguments);
            Check(value.GetProperty("retainAcceptedBindings").ValueKind == JsonValueKind.True,
                "normal local activation preserves already accepted workflow bindings");
            defaultModel = value.GetProperty("modelId").GetString()!; defaultWorker = value.GetProperty("modelWorkerId").GetString()!;
            return Json(new { });
        }
        using var plugin = Create(host);
        var button = StatusButton(plugin);
        Menu(plugin, "resume-local").ExecuteAsync(default).GetAwaiter().GetResult();
        Check(Label(button) == "Local GPU active",
            "activation completes with a confirmed active local GPU; actual label was " + Label(button));
        CheckPresentation(button, Brushes.LimeGreen, "Local GPU active", "Ready");
        Check(host.Operations.Contains("models.default.switch") && !host.Operations.Contains("engine.resume"),
            "activation confirms a capable local default while preserving an already-paused engine");
    }

    private static DesktopServiceCommandResult LocalDefaults() => Json(new { defaultModelId = "local-text", defaultModelWorkerId = "local" });

    private static DesktopServiceCommandResult Json(object value) =>
        new(true, JsonSerializer.SerializeToElement(value, new JsonSerializerOptions(JsonSerializerDefaults.Web)));

    private static void RefreshFailureClearsReadiness()
    {
        var host = new FixtureHost { Reply = (_, _, _) => Task.FromResult(Status("paused")) };
        using var plugin = Create(host);
        var button = StatusButton(plugin);
        host.Reply = (_, _, _) => Task.FromResult(new DesktopServiceCommandResult(false, default, "fixture disconnected"));
        try { Menu(plugin, "refresh-status").ExecuteAsync(default).GetAwaiter().GetResult(); }
        catch (InvalidOperationException) { }
        CheckPresentation(button, Brushes.IndianRed, "Local GPU unavailable", "Needs attention");
        Check(button.IsEnabled && button.ToolTip!.ToString()!.Contains("fixture disconnected"),
            "unavailable state exposes the error and permits an explicit status retry");
        host.Reply = (_, _, _) => Task.FromResult(Status("paused"));
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        CheckPresentation(button, Brushes.LimeGreen, "Local GPU inactive", "Ready");
        Check(!host.Operations.Contains("resources.local.resume"),
            "retry after loss of confirmation reads status without resuming the GPU");
    }

    private static void PartialStatusCannotImplyReady()
    {
        var host = new FixtureHost { Reply = (_, _, _) => Task.FromResult(
            new DesktopServiceCommandResult(true, JsonSerializer.SerializeToElement(new { state = "paused" }))) };
        using var plugin = Create(host);
        var button = StatusButton(plugin);
        CheckPresentation(button, Brushes.IndianRed, "Local GPU unavailable", "Needs attention");
        Check(!AutomationProperties.GetName(button).Contains("Ready") &&
              host.Operations.SequenceEqual(["resources.local.status"]),
            "a partial paused response cannot imply zero active local assignments or readiness");
    }

    private static void TransitionFailureClearsReadiness()
    {
        var host = new FixtureHost
        {
            Reply = (operation, _, _) => Task.FromResult(operation == "resources.local.pause"
                ? new DesktopServiceCommandResult(false, default, "fixture pause denied")
                : operation == "settings.get" ? LocalDefaults() : Status("active"))
        };
        using var plugin = Create(host);
        var button = StatusButton(plugin);
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        CheckPresentation(button, Brushes.IndianRed, "Local GPU unavailable", "Needs attention");
        Check(button.ToolTip!.ToString()!.Contains("fixture pause denied"),
            "a rejected transition cannot retain the prior ready indicator");
    }

    private static ResourcePausePlugin Create(FixtureHost host)
    {
        var plugin = new ResourcePausePlugin();
        plugin.Initialize(host);
        return plugin;
    }

    private static Button StatusButton(ResourcePausePlugin plugin) =>
        (Button)plugin.MountContributions.OfType<DesktopMountView>()
            .Single(item => item.Descriptor.Id == "resource-status").CreateView();

    private static DesktopMountAction Menu(ResourcePausePlugin plugin, string id) =>
        plugin.MountContributions.OfType<DesktopMountAction>().Single(item => item.Descriptor.Id == id);

    private static Ellipse Indicator(Button button) => (Ellipse)((StackPanel)button.Content).Children[0];
    private static TextBlock LabelView(Button button) => (TextBlock)((StackPanel)button.Content).Children[1];
    private static string Label(Button button) => LabelView(button).Text;

    private static void CheckPresentation(Button button, SolidColorBrush brush, string label, string meaning)
    {
        Check(Indicator(button).Fill is SolidColorBrush actual && actual.Color == brush.Color &&
              Label(button) == label && AutomationProperties.GetName(button) == label + ". " + meaning + "." &&
              AutomationProperties.GetName(Indicator(button)) == meaning,
            label + " has consistent visible color, wording and accessible readiness");
    }

    private static DesktopServiceCommandResult Status(string state, int active = 0, int queued = 0) =>
        new(true, JsonSerializer.SerializeToElement(new
        {
            state, activeLocalAssignments = active, queuedLocalAssignments = queued,
            queuedLocalWorkflows = 0, peers = Array.Empty<object>(), detail = $"fixture detail; queued {queued}"
        }));

    private static void PumpUntil(Func<bool> ready)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(3);
        while (!ready())
        {
            if (DateTime.UtcNow >= deadline) throw new TimeoutException("Fixture UI update did not complete.");
            var frame = new DispatcherFrame();
            var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(10) };
            timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
            timer.Start();
            Dispatcher.PushFrame(frame);
            timer.Stop();
        }
    }

    private static void Check(bool condition, string label)
    {
        if (!condition) throw new Exception("FAIL: " + label);
        checks++;
        Console.WriteLine("PASS: " + label);
    }

    private sealed partial class FixtureHost : IDesktopPluginHost, IDesktopServiceHost, IDesktopPluginEnvironment
    {
        public required Func<string, object, CancellationToken, Task<DesktopServiceCommandResult>> Reply { get; set; }
        public List<string> Operations { get; } = [];
        public List<(string Operation, JsonElement Arguments)> Calls { get; } = [];
        public bool PauseReroute { get; private set; }
        public DesktopPluginSurface Surface => DesktopPluginSurface.Configuration;
        public string DataDirectory { get; } = System.IO.Path.Combine(TestDataRoot, Guid.NewGuid().ToString("N"));
        public string PackageDirectory => AppContext.BaseDirectory;
        public event EventHandler<DesktopConversationChangedEventArgs>? ConversationChanged { add { } remove { } }
        public event EventHandler<DesktopLeadMessageEventArgs>? LeadMessageCommitted { add { } remove { } }
        public DesktopConversationSnapshot GetConversation() => throw new NotSupportedException();
        public DesktopDraftSnapshot GetDraft() => throw new NotSupportedException();
        public Task<DesktopOperationResult> ReplaceDraftAsync(DesktopDraftUpdate update, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<DesktopOperationResult> SubmitAsync(DesktopSubmission submission, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<DesktopOperationResult> SteerAsync(DesktopSubmission submission, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<DesktopOperationResult> StopAsync(DesktopConversationBinding binding, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public void DismissInputGuidance() => throw new NotSupportedException();
        public void RefreshPresentation() => throw new NotSupportedException();
        public void ReportStatus(string text) { }
        public Task<DesktopServiceCommandResult> ExecuteServiceCommandAsync(string operation, object arguments, CancellationToken cancellationToken = default)
        {
            Operations.Add(operation);
            Calls.Add((operation, JsonSerializer.SerializeToElement(arguments)));
            if (operation == "resources.local.pause")
                PauseReroute = JsonSerializer.SerializeToElement(arguments).GetProperty("rerouteEligibleWork").GetBoolean();
            cancellationToken.ThrowIfCancellationRequested();
            if (IsSettingsOperation(operation))
                return SettingsReply?.Invoke(operation, arguments, cancellationToken) ??
                    Task.FromResult(DefaultSettingsReply(operation, arguments, cancellationToken));
            return Reply(operation, arguments, cancellationToken);
        }
    }
}
