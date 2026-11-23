using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Cerebral.Desktop.Plugins;

namespace Cerebral.ResourcePausePlugin.Tests;

public static partial class Program
{
    private static void RunRoutingChecks()
    {
        ActiveRemoteRouteCanActivateLocal();
        ActiveRemotePanelCanActivateLocal();
        UnknownDefaultRouteOnlyRefreshes();
        MissingDefaultIsActionable();
        EarlierRouteReadCannotOverwriteActivation();
    }

    private static void ActiveRemoteRouteCanActivateLocal()
    {
        var fixture = new LocalRoutingFixture();
        using var plugin = Create(fixture.Host);
        var button = StatusButton(plugin);
        CheckPresentation(button, Brushes.IndianRed, "Local GPU active · remote default", "Needs attention");
        Check(button.ToolTip!.ToString()!.Contains("remote-model on gb10", StringComparison.Ordinal) &&
            fixture.Host.Operations.SequenceEqual(["resources.local.status", "settings.get"]),
            "startup reports the actual remote default using reads only, without changing admission or routing");
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        PumpUntil(() => Label(button) == "Local GPU active");
        CheckPresentation(button, Brushes.LimeGreen, "Local GPU active", "Ready");
        Check(fixture.Host.Operations.Where(IsRoutingMutation).SequenceEqual(["engine.pause", "models.default.switch", "engine.resume"]) &&
            fixture.DefaultWorker == "local" && fixture.DefaultModel == "local-text",
            "the normal toggle repairs active GPU plus remote default under the existing engine hold without a GPU pause/resume cycle");
        Check(!fixture.Host.Operations.Any(operation => operation.StartsWith("agents.", StringComparison.Ordinal) ||
            operation.StartsWith("workflow.", StringComparison.Ordinal)),
            "local activation writes no individual profile or accepted workflow records");
    }

    private static void ActiveRemotePanelCanActivateLocal()
    {
        var fixture = new LocalRoutingFixture { EnginePaused = true };
        using var plugin = Create(fixture.Host);
        var panel = plugin.ConfigurationPanels.Single(item => item.Id == "local-gpu").CreateView();
        var activate = Find<Button>(panel, "ResourcePause.Resume");
        Check(activate.IsEnabled, "the explicit preferred-local action is available when admission is already active");
        activate.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        PumpUntil(() => fixture.DefaultWorker == "local");
        Check(!fixture.Host.Operations.Contains("engine.resume"),
            "active-state panel activation keeps a pre-existing engine pause");
    }

    private static void UnknownDefaultRouteOnlyRefreshes()
    {
        foreach (var malformed in new[] { false, true })
        {
            var fixture = new LocalRoutingFixture { DefaultWorker = "local", DefaultModel = "local-text" };
            fixture.ReadDefaults = count => Task.FromResult(count == 1
                ? malformed ? Data(new { defaultModelId = "unbound" }) : new DesktopServiceCommandResult(false, default, "fixture defaults unavailable")
                : Data(new { defaultModelId = fixture.DefaultModel, defaultModelWorkerId = fixture.DefaultWorker }));
            using var plugin = Create(fixture.Host);
            var button = StatusButton(plugin);
            CheckPresentation(button, Brushes.Gray, "Local model route unknown", "Unknown");
            Check(!button.ToolTip!.ToString()!.Contains("route is remote", StringComparison.Ordinal) &&
                button.ToolTip.ToString()!.Contains("could not be confirmed", StringComparison.Ordinal),
                "a missing or failed defaults read does not claim local readiness or invent a remote route");
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            PumpUntil(() => Label(button) == "Local GPU active");
            Check(!fixture.Host.Operations.Any(IsRoutingMutation),
                "the unknown-route toggle retries observation without changing resources, engine admission, or model routing");
        }
    }

    private static void MissingDefaultIsActionable()
    {
        var fixture = new LocalRoutingFixture { DefaultWorker = "local", DefaultModel = "" };
        using var plugin = Create(fixture.Host);
        var button = StatusButton(plugin);
        CheckPresentation(button, Brushes.IndianRed, "Local GPU active · no default", "Needs attention");
        Check(button.ToolTip!.ToString()!.Contains("No default model is selected", StringComparison.Ordinal),
            "a known empty default is actionable without being described as remote");
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        PumpUntil(() => Label(button) == "Local GPU active");
        Check(fixture.DefaultModel == "local-text", "normal activation can select the unique capable local model from an empty default");
    }

    private static void EarlierRouteReadCannotOverwriteActivation()
    {
        var fixture = new LocalRoutingFixture();
        var earlier = new TaskCompletionSource<DesktopServiceCommandResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.ReadDefaults = count => count == 2 ? earlier.Task :
            Task.FromResult(Data(new { defaultModelId = fixture.DefaultModel, defaultModelWorkerId = fixture.DefaultWorker }));
        using var plugin = Create(fixture.Host);
        var button = StatusButton(plugin);
        var staleRefresh = Menu(plugin, "refresh-status").ExecuteAsync(default);
        Check(fixture.DefaultReads == 2 && !staleRefresh.IsCompleted, "the fixture retains an earlier unresolved remote route read");
        Menu(plugin, "resume-local").ExecuteAsync(default).GetAwaiter().GetResult();
        CheckPresentation(button, Brushes.LimeGreen, "Local GPU active", "Ready");
        earlier.SetResult(Data(new { defaultModelId = "remote-model", defaultModelWorkerId = "gb10" }));
        PumpUntil(() => staleRefresh.IsCompleted);
        staleRefresh.GetAwaiter().GetResult();
        CheckPresentation(button, Brushes.LimeGreen, "Local GPU active", "Ready");
        Check(button.ToolTip!.ToString()!.Contains("route is local: local-text", StringComparison.Ordinal) &&
            fixture.Host.Operations.Count(operation => operation == "models.default.switch") == 1,
            "an earlier route observation cannot overwrite a confirmed local activation or repeat its write");
    }

    private static bool IsRoutingMutation(string operation) =>
        operation is "engine.pause" or "engine.resume" or "models.default.switch" or "resources.local.pause" or "resources.local.resume";

    private sealed class LocalRoutingFixture
    {
        public string DefaultWorker = "gb10";
        public string DefaultModel = "remote-model";
        public bool EnginePaused;
        public int DefaultReads;
        public Func<int, Task<DesktopServiceCommandResult>>? ReadDefaults;
        public FixtureHost Host { get; }

        public LocalRoutingFixture() => Host = new FixtureHost { Reply = Reply };

        private Task<DesktopServiceCommandResult> Reply(string operation, object arguments, CancellationToken cancellationToken)
        {
            if (operation == "settings.get")
            {
                var count = ++DefaultReads;
                return ReadDefaults?.Invoke(count) ??
                    Task.FromResult(Data(new { defaultModelId = DefaultModel, defaultModelWorkerId = DefaultWorker }));
            }
            return Task.FromResult(operation switch
            {
                "resources.local.status" => Status("active"),
                "models.list" => Models(Model("local-text", "Local Text", usable: true, state: "loaded")),
                "engine.status" => Engine(EnginePaused, 19),
                "engine.pause" => Engine(EnginePaused = true, 19),
                "engine.resume" => Engine(EnginePaused = false, 19),
                "models.default.switch" => Switch(arguments),
                _ => throw new InvalidOperationException("Unexpected routing fixture operation " + operation)
            });
        }

        private DesktopServiceCommandResult Switch(object arguments)
        {
            var value = JsonSerializer.SerializeToElement(arguments);
            Check(EnginePaused && value.GetProperty("retainAcceptedBindings").ValueKind == JsonValueKind.True &&
                value.GetProperty("modelWorkerId").GetString() == "local",
                "active-state local activation holds admission and requests accepted-binding preservation on the exact local route");
            DefaultWorker = "local";
            DefaultModel = value.GetProperty("modelId").GetString()!;
            return Data(new { });
        }
    }
}
