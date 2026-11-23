using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using Cerebral.Desktop.Plugins;

namespace Cerebral.ResourcePausePlugin.Tests;

public static partial class Program
{
    private static void RunAdvancedChecks()
    {
        LocalActivationUsesSafeOrdering();
        LocalActivationFailureKeepsEngineHeld();
        AmbiguousLocalChoiceRequiresAdvancedSelection();
        AdvancedSelectionFiltersAndPersistsPreference();
        EngineStatusAndExactMutationArguments();
        MountedEngineStatusConvergesAndStops();
        StaleEngineRefreshCannotOverwriteMutation();
        EngineRollbackUsesExactEpoch();
        EngineSwitchDrainsOnlyObservedRunningWork();
        EngineSwitchRejectsUnboundedActivePages();
        EngineMalformedStatusFailsClosed();
        WorkHoldStartupStateIsReadOnlyAndScoped();
        WorkHoldCreationUsesExactCas();
        WorkHoldResolutionUsesExactCas();
        WorkHoldMalformedStatusFailsClosed();
        RunPrimaryStatusChecks();
    }

    private static void LocalActivationUsesSafeOrdering()
    {
        var resource = "paused"; var enginePaused = false; var defaultModel = "remote-model"; var defaultWorker = "gb10";
        var acceptedRoute = (WorkerId: "gb10", ModelId: "remote-model");
        var host = new FixtureHost
        {
            Reply = (operation, arguments, _) => Task.FromResult(operation switch
            {
                "resources.local.status" => Status(resource),
                "settings.get" => Data(new { defaultModelId = defaultModel, defaultModelWorkerId = defaultWorker }),
                "models.list" => Models(Model("local-text", "Local Text", usable: true)),
                "engine.status" => Engine(enginePaused, 7),
                "engine.pause" => Engine(enginePaused = true, 7),
                "resources.local.resume" => Status(resource = "active"),
                "models.default.switch" => Switch(arguments),
                "engine.resume" => Engine(enginePaused = false, 7),
                _ => throw new InvalidOperationException("Unexpected fixture operation " + operation)
            })
        };
        DesktopServiceCommandResult Switch(object arguments)
        {
            var value = JsonSerializer.SerializeToElement(arguments);
            Check(value.GetProperty("modelId").GetString() == "local-text" && value.GetProperty("modelWorkerId").GetString() == "local" &&
                  value.GetProperty("retainAcceptedBindings").ValueKind == JsonValueKind.True,
                "local activation requests the exact selected local route for future admissions while retaining accepted bindings");
            defaultModel = "local-text"; defaultWorker = "local"; return Data(new { });
        }
        using var plugin = Create(host);
        var button = StatusButton(plugin);
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        PumpUntil(() => Label(button) == "Local GPU active");
        var expected = new[]
        {
            "resources.local.status", "configuration.settings.describe", "settings.plugins.transfer.get",
            "resources.local.status", "settings.get", "models.list", "engine.status",
            "engine.pause", "resources.local.resume", "models.default.switch", "settings.get", "engine.status",
            "engine.resume", "resources.local.status"
        };
        Check(host.Operations.SequenceEqual(expected),
            "normal activation pauses the engine before GPU resume, verifies the local default, then restores the prior active engine");
        Check(host.Calls.Single(call => call.Operation == "engine.pause").Arguments.GetProperty("expectedEpoch").GetInt64() == 7 &&
              host.Calls.Single(call => call.Operation == "engine.resume").Arguments.GetProperty("expectedEpoch").GetInt64() == 7,
            "local activation binds both engine mutations to the exact observed epoch");
        Check(acceptedRoute == ("gb10", "remote-model") && defaultWorker == "local" && defaultModel == "local-text",
            "the retained-routing fixture keeps the accepted run's remote route while the saved default moves to local for future admissions");
    }

    private static void LocalActivationFailureKeepsEngineHeld()
    {
        var resource = "paused"; var enginePaused = false;
        var host = new FixtureHost
        {
            Reply = (operation, _, _) => Task.FromResult(operation switch
            {
                "resources.local.status" => Status(resource),
                "settings.get" => Data(new { defaultModelId = "remote", defaultModelWorkerId = "gb10" }),
                "models.list" => Models(Model("local-text", "Local Text", usable: true)),
                "engine.status" => Engine(enginePaused, 11),
                "engine.pause" => Engine(enginePaused = true, 11),
                "resources.local.resume" => Status(resource = "active"),
                "models.default.switch" => new DesktopServiceCommandResult(false, default, "fixture switch denied"),
                _ => throw new InvalidOperationException("Unexpected fixture operation " + operation)
            })
        };
        using var plugin = Create(host);
        var engineView = plugin.MountContributions.OfType<DesktopMountView>()
            .Single(item => item.Descriptor.Id == "engine-status").CreateView();
        var engineStatus = Find<Button>(engineView, "ResourcePause.EngineStatusToggle");
        PumpUntil(() => AutomationProperties.GetName(engineStatus).StartsWith("Engine A active", StringComparison.Ordinal));
        var button = StatusButton(plugin);
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        PumpUntil(() => Label(button) == "Local activation incomplete");
        Check(!host.Operations.Contains("engine.resume") && enginePaused &&
              AutomationProperties.GetName(engineStatus).StartsWith("Engine A paused", StringComparison.Ordinal),
            "a failed local switch leaves the established engine hold visible and does not blind-retry or resume");
        Check(button.ToolTip!.ToString()!.Contains("fixture switch denied") &&
              button.ToolTip.ToString()!.Contains("no ambiguous model write was replayed", StringComparison.OrdinalIgnoreCase),
            "a partial local transition exposes the bounded cause and recovery boundary");
    }

    private static void AmbiguousLocalChoiceRequiresAdvancedSelection()
    {
        var host = new FixtureHost
        {
            Reply = (operation, _, _) => Task.FromResult(operation switch
            {
                "resources.local.status" => Status("paused"),
                "settings.get" => Data(new { defaultModelId = "remote", defaultModelWorkerId = "gb10" }),
                "models.list" => Models(Model("local-a", "Local A", true), Model("local-b", "Local B", true)),
                _ => throw new InvalidOperationException("Unexpected fixture operation " + operation)
            })
        };
        using var plugin = Create(host);
        var button = StatusButton(plugin); button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        PumpUntil(() => Label(button) == "Local activation incomplete");
        Check(!host.Operations.Any(operation => operation is "engine.pause" or "resources.local.resume" or "models.default.switch") &&
              button.ToolTip!.ToString()!.Contains("Advanced configuration"),
            "ambiguous capable local models require an explicit Advanced preference before any mutation");
    }

    private static void AdvancedSelectionFiltersAndPersistsPreference()
    {
        var resource = "paused"; var enginePaused = true; var defaultModel = "remote"; var defaultWorker = "gb10";
        var catalog = new[]
        {
            Model("chosen", "Chosen", true), Model("not-text", "No text", false),
            Model("failed", "Failed", true, "failed", "fixture error"), Model("duplicate", "Duplicate one", true),
            Model("duplicate", "Duplicate two", true)
        };
        var host = new FixtureHost
        {
            Reply = (operation, arguments, _) => Task.FromResult(operation switch
            {
                "models.list" => Models(catalog),
                "resources.local.status" => Status(resource),
                "settings.get" => Data(new { defaultModelId = defaultModel, defaultModelWorkerId = defaultWorker }),
                "engine.status" => Engine(enginePaused, 13),
                "resources.local.resume" => Status(resource = "active"),
                "models.default.switch" => ConfirmSwitch(arguments),
                _ => throw new InvalidOperationException("Unexpected fixture operation " + operation)
            })
        };
        DesktopServiceCommandResult ConfirmSwitch(object arguments)
        {
            var value = JsonSerializer.SerializeToElement(arguments); defaultModel = value.GetProperty("modelId").GetString()!;
            defaultWorker = value.GetProperty("modelWorkerId").GetString()!;
            Check(value.GetProperty("retainAcceptedBindings").ValueKind == JsonValueKind.True,
                "Advanced local activation uses the same accepted-binding preservation boundary");
            return Data(new { });
        }
        using var plugin = Create(host);
        var view = plugin.ConfigurationPanels.Single(panel => panel.Id == "local-model").CreateView();
        var selector = Find<ComboBox>(view, "ResourcePause.LocalModelSelector");
        var use = Find<Button>(view, "ResourcePause.UseLocalModel");
        PumpUntil(() => selector.IsEnabled);
        Check(selector.Items.Count == 1 && selector.SelectedValue is null,
            "Advanced local selection filters unusable/duplicate registrations without silently saving or selecting a preference");
        var save = Find<Button>(view, "ResourcePauseSavePreferences");
        PumpUntil(() => save.IsEnabled);
        selector.SelectedValue = "chosen";
        save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        PumpUntil(() => host.SettingsState.Active?.Values.GetProperty("preferredLocalModelId").GetString() == "chosen");
        Check(host.SettingsState.Revision == 1 && host.SettingsState.Coordinate.PluginId == "pegasus.resource-pause" &&
              host.SettingsState.Active!.SourcePackageVersion == "1.1.5" &&
              !File.Exists(Path.Combine(host.DataDirectory, "resource-pause.json")) &&
              !host.Operations.Any(operation => operation is "resources.local.resume" or "models.default.switch" or "engine.resume"),
            "explicit Save preferences commits the scoped central choice without activating a model or writing a legacy local file");
        use.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        PumpUntil(() => host.Operations.Contains("models.default.switch") && use.IsEnabled);
        Check(host.SettingsState.Active!.Values.GetProperty("preferredLocalModelId").GetString() == "chosen" &&
              defaultModel == "chosen" && defaultWorker == "local",
            "explicit activation uses the previously acknowledged central model preference");
        Check(!host.Operations.Contains("engine.resume"),
            "local activation preserves a pre-existing engine pause instead of reopening workflow admission");
    }

    private static void EngineStatusAndExactMutationArguments()
    {
        var paused = false;
        var host = new FixtureHost
        {
            Reply = (operation, _, _) => Task.FromResult(operation switch
            {
                "engine.status" => Engine(paused, 17),
                "engine.pause" => Engine(paused = true, 17),
                _ => throw new InvalidOperationException("Unexpected fixture operation " + operation)
            })
        };
        using var plugin = Create(host);
        var view = plugin.MountContributions.OfType<DesktopMountView>().Single(item => item.Descriptor.Id == "engine-status").CreateView();
        var status = Find<Button>(view, "ResourcePause.EngineStatusToggle");
        PumpUntil(() => AutomationProperties.GetName(status).StartsWith("Engine A active", StringComparison.Ordinal));
        Check(Indicator(status).Fill is SolidColorBrush brush && brush.Color == Brushes.LimeGreen.Color &&
              host.Operations.SequenceEqual(["engine.status"]),
            "engine status-bar readiness is read-only, accessible, and green only after a healthy exact generation");
        var panel = plugin.ConfigurationPanels.Single(item => item.Id == "engine-control").CreateView();
        var pause = Find<Button>(panel, "ResourcePause.EnginePauseResume");
        PumpUntil(() => pause.IsEnabled);
        pause.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        PumpUntil(() => AutomationProperties.GetName(status).StartsWith("Engine A paused", StringComparison.Ordinal));
        Check(host.Calls.Single(call => call.Operation == "engine.pause").Arguments.GetProperty("expectedEpoch").GetInt64() == 17,
            "engine pause uses the exact displayed epoch and returns a confirmed paused state");
    }

    private static void MountedEngineStatusConvergesAndStops()
    {
        var paused = true; var reads = 0; var concurrent = 0; var maximumConcurrent = 0;
        var block = false; var cancelled = false;
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<DesktopServiceCommandResult> Reply(CancellationToken token)
        {
            reads++;
            var active = Interlocked.Increment(ref concurrent);
            maximumConcurrent = Math.Max(maximumConcurrent, active);
            try
            {
                if (block)
                {
                    entered.TrySetResult(true);
                    try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
                    catch (OperationCanceledException) when (token.IsCancellationRequested) { cancelled = true; throw; }
                }
                return Engine(paused, 18);
            }
            finally { Interlocked.Decrement(ref concurrent); }
        }
        var host = new FixtureHost
        {
            Reply = (operation, _, token) => operation == "engine.status"
                ? Reply(token)
                : throw new InvalidOperationException("Unexpected fixture operation " + operation)
        };
        using var plugin = Create(host);
        var view = plugin.MountContributions.OfType<DesktopMountView>()
            .Single(item => item.Descriptor.Id == "engine-status").CreateView();
        var status = Find<Button>(view, "ResourcePause.EngineStatusToggle");
        PumpUntil(() => AutomationProperties.GetName(status).StartsWith("Engine A paused", StringComparison.Ordinal));
        view.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
        PumpUntil(() => reads >= 2);
        paused = false;
        PumpUntil(() => reads >= 3 && AutomationProperties.GetName(status).StartsWith("Engine A active", StringComparison.Ordinal));
        Check(maximumConcurrent == 1 && host.Operations.All(operation => operation == "engine.status"),
            "a loaded engine indicator converges after an external state change using read-only nonoverlapping status calls");

        block = true;
        PumpUntil(() => entered.Task.IsCompleted);
        var blockedReads = reads;
        var overlapDeadline = DateTime.UtcNow + TimeSpan.FromMilliseconds(1200);
        PumpUntil(() => DateTime.UtcNow >= overlapDeadline);
        Check(reads == blockedReads && maximumConcurrent == 1,
            "a slow mounted status read does not admit overlapping one-second refresh calls");
        view.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
        PumpUntil(() => cancelled && Volatile.Read(ref concurrent) == 0);
        view.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
        var unloadedReads = reads;
        var unloadedDeadline = DateTime.UtcNow + TimeSpan.FromMilliseconds(1200);
        PumpUntil(() => DateTime.UtcNow >= unloadedDeadline);
        Check(reads == unloadedReads, "unloading the engine indicator cancels its read and repeated unload remains inert");

        block = false;
        view.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
        PumpUntil(() => reads > unloadedReads && AutomationProperties.GetName(status).StartsWith("Engine A active", StringComparison.Ordinal));
        view.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
        Check(maximumConcurrent == 1, "reloading the same indicator starts one fresh bounded monitor without retaining the cancelled one");
    }

    private static void EngineRollbackUsesExactEpoch()
    {
        var candidate = true; var epoch = 31L; var activeSlot = "B";
        var host = new FixtureHost
        {
            Reply = (operation, arguments, _) => Task.FromResult(operation switch
            {
                "engine.status" => Engine(true, epoch, activeSlot, candidate),
                "engine.rollback" => Rollback(arguments),
                _ => throw new InvalidOperationException("Unexpected fixture operation " + operation)
            })
        };
        DesktopServiceCommandResult Rollback(object arguments)
        {
            Check(JsonSerializer.SerializeToElement(arguments).GetProperty("expectedEpoch").GetInt64() == 31,
                "engine rollback binds the request to the exact displayed candidate epoch");
            candidate = false; epoch = 32; activeSlot = "A";
            return Engine(true, epoch, activeSlot, candidate);
        }
        using var plugin = Create(host);
        var statusView = plugin.MountContributions.OfType<DesktopMountView>()
            .Single(item => item.Descriptor.Id == "engine-status").CreateView();
        var status = Find<Button>(statusView, "ResourcePause.EngineStatusToggle");
        var panel = plugin.ConfigurationPanels.Single(item => item.Id == "engine-control").CreateView();
        var rollback = Find<Button>(panel, "ResourcePause.EngineRollback");
        PumpUntil(() => rollback.IsEnabled && AutomationProperties.GetName(status).StartsWith("Engine B review", StringComparison.Ordinal));
        Check(Indicator(status).Fill is SolidColorBrush pending && pending.Color == Brushes.Gold.Color,
            "an unconfirmed engine candidate is yellow and requires an explicit resume or rollback decision");
        rollback.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        PumpUntil(() => !rollback.IsEnabled && AutomationProperties.GetName(status).StartsWith("Engine A paused", StringComparison.Ordinal));
        Check(!host.Operations.Contains("engine.resume") &&
              Indicator(status).Fill is SolidColorBrush held && held.Color == Brushes.Gold.Color,
            "confirmed rollback stays yellow at a held generation without reopening engine admission");
    }

    private static void StaleEngineRefreshCannotOverwriteMutation()
    {
        var statusReads = 0; var paused = false;
        var stale = new TaskCompletionSource<DesktopServiceCommandResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var host = new FixtureHost
        {
            Reply = (operation, _, _) => operation switch
            {
                "engine.status" when ++statusReads == 2 => stale.Task,
                "engine.status" => Task.FromResult(Engine(paused, 19)),
                "engine.pause" => Task.FromResult(Engine(paused = true, 19)),
                _ => throw new InvalidOperationException("Unexpected fixture operation " + operation)
            }
        };
        using var plugin = Create(host);
        var panel = plugin.ConfigurationPanels.Single(item => item.Id == "engine-control").CreateView();
        var pause = Find<Button>(panel, "ResourcePause.EnginePauseResume");
        var refresh = Find<Button>(panel, "ResourcePause.EngineRefresh");
        var summary = Find<TextBlock>(panel, "ResourcePause.EngineSummary");
        PumpUntil(() => pause.IsEnabled);
        refresh.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        PumpUntil(() => statusReads == 2);
        pause.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        PumpUntil(() => summary.Text.Contains("held boundary at epoch 19", StringComparison.Ordinal));
        stale.SetResult(Engine(false, 19));
        Application.Current.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Background);
        Check(summary.Text.Contains("held boundary at epoch 19", StringComparison.Ordinal) && pause.Content?.ToString() == "Resume engine",
            "a status read begun before an engine mutation cannot overwrite the later confirmed state");
    }

    private static void EngineSwitchDrainsOnlyObservedRunningWork()
    {
        var paused = false; var workflowReads = 0;
        var host = new FixtureHost
        {
            Reply = (operation, _, _) => Task.FromResult(operation switch
            {
                "engine.status" => Engine(paused, 23),
                "engine.pause" => Engine(paused = true, 23),
                "jobs.workflow.active-summaries" => WorkflowPage(workflowReads++ == 0 ? new[] { new { id = "run-1", status = "running" }, new { id = "old-wait", status = "waiting" } } : []),
                "queue.active-summaries" => LegacyPage([]),
                "engine.activate" => Engine(true, 24, activeSlot: "B", candidatePending: true),
                _ => throw new InvalidOperationException("Unexpected fixture operation " + operation)
            })
        };
        using var plugin = Create(host);
        var panel = plugin.ConfigurationPanels.Single(item => item.Id == "engine-control").CreateView();
        var change = Find<Button>(panel, "ResourcePause.EngineSwitch");
        PumpUntil(() => change.IsEnabled);
        change.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        PumpUntil(() => host.Operations.Contains("engine.activate"));
        var activation = host.Calls.Single(call => call.Operation == "engine.activate").Arguments;
        Check(activation.GetProperty("slot").GetString() == "B" && activation.GetProperty("expectedEpoch").GetInt64() == 23,
            "engine switching pauses first, waits for the exact observed running ID, and activates the selected alternate at the captured epoch");
        Check(workflowReads == 2 && host.Operations.Count(operation => operation == "queue.active-summaries") == 2 &&
              !host.Operations.Contains("jobs.workflow.cancel") && !host.Operations.Contains("engine.resume"),
            "engine drain ignores historical Waiting rows, does not cancel work, and leaves the candidate paused for review");
    }

    private static void EngineSwitchRejectsUnboundedActivePages()
    {
        var paused = false;
        var host = new FixtureHost
        {
            Reply = (operation, arguments, _) => Task.FromResult(operation switch
            {
                "engine.status" => Engine(paused, 29),
                "engine.pause" => Engine(paused = true, 29),
                "jobs.workflow.active-summaries" => WorkflowPage([], JsonSerializer.SerializeToElement(arguments).GetProperty("afterRow").GetInt64() + 1),
                _ => throw new InvalidOperationException("Unexpected fixture operation " + operation)
            })
        };
        using var plugin = Create(host);
        var panel = plugin.ConfigurationPanels.Single(item => item.Id == "engine-control").CreateView();
        var change = Find<Button>(panel, "ResourcePause.EngineSwitch");
        var summary = Find<TextBlock>(panel, "ResourcePause.EngineSummary");
        PumpUntil(() => change.IsEnabled); change.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        PumpUntil(() => summary.Text.Contains("could not be bounded", StringComparison.Ordinal));
        Check(!host.Operations.Contains("engine.activate") && paused,
            "unbounded active-work pagination fails closed with the engine held and no activation");
    }

    private static void EngineMalformedStatusFailsClosed()
    {
        var host = new FixtureHost
        {
            Reply = (operation, _, _) => Task.FromResult(operation switch
            {
                "resources.local.status" => Status("active"),
                "settings.get" => LocalDefaults(),
                "engine.status" => Data(new { paused = false, epoch = 4 }),
                _ => throw new InvalidOperationException("Unexpected fixture operation " + operation)
            })
        };
        using var plugin = Create(host);
        var local = StatusButton(plugin);
        PumpUntil(() => Label(local) == "Local GPU active");
        var view = plugin.MountContributions.OfType<DesktopMountView>().Single(item => item.Descriptor.Id == "engine-status").CreateView();
        var status = Find<Button>(view, "ResourcePause.EngineStatusToggle");
        PumpUntil(() => AutomationProperties.GetName(status).StartsWith("Engine attention", StringComparison.Ordinal));
        Check(Indicator(status).Fill is SolidColorBrush brush && brush.Color == Brushes.IndianRed.Color &&
              AutomationProperties.GetHelpText(status).Contains("healthy, identifiable engine") && Label(local) == "Local GPU active",
            "an incomplete engine projection renders red without erasing independently confirmed local GPU readiness");
    }

    private static void WorkHoldStartupStateIsReadOnlyAndScoped()
    {
        var held = Hold("held", generation: 6, revision: 12, startup: true,
            work: [HeldWork("workflow", "run-owned", executing: false, requiresReview: true)]);
        var host = new FixtureHost
        {
            Reply = (operation, _, _) => Task.FromResult(operation == "engine.work-hold.status"
                ? held : throw new InvalidOperationException("Unexpected fixture operation " + operation))
        };
        using var plugin = Create(host);
        var statusView = plugin.MountContributions.OfType<DesktopMountView>()
            .Single(item => item.Descriptor.Id == "work-hold-status").CreateView();
        var status = Find<Button>(statusView, "ResourcePause.WorkHoldStatus");
        var panel = plugin.ConfigurationPanels.Single(item => item.Id == "work-hold").CreateView();
        var stop = Find<Button>(panel, "ResourcePause.WorkHoldStopNow");
        var finish = Find<Button>(panel, "ResourcePause.WorkHoldFinishCurrent");
        var resume = Find<Button>(panel, "ResourcePause.WorkHoldResume");
        var cancel = Find<Button>(panel, "ResourcePause.WorkHoldCancel");
        PumpUntil(() => resume.IsEnabled && cancel.IsEnabled);
        Check(Label(status) == "Held work needs review" &&
              Indicator(status).Fill is SolidColorBrush brush && brush.Color == Brushes.Gold.Color && !stop.IsEnabled && !finish.IsEnabled,
            "startup reports only an explicit persisted work hold and requires an owner decision without mutating it");
        Check(host.Operations.All(operation => operation == "engine.work-hold.status") && host.Operations.Count == 2,
            "work-hold startup performs bounded status reads only and never scans or changes historical Waiting runs");
    }

    private static void WorkHoldCreationUsesExactCas()
    {
        AssertCreation("stop-now", "ResourcePause.WorkHoldStopNow");
        AssertCreation("finish-current", "ResourcePause.WorkHoldFinishCurrent");

        static void AssertCreation(string mode, string automationId)
        {
            var current = Hold("none", generation: 4, revision: 9, holdId: null, requestId: null, mode: null, enginePaused: false);
            JsonElement admitted = default;
            var host = new FixtureHost
            {
                Reply = (operation, arguments, _) => Task.FromResult(operation switch
                {
                    "engine.work-hold.status" => current,
                    "engine.work-hold" => Admit(arguments),
                    _ => throw new InvalidOperationException("Unexpected fixture operation " + operation)
                })
            };
            DesktopServiceCommandResult Admit(object arguments)
            {
                admitted = JsonSerializer.SerializeToElement(arguments);
                var request = admitted.GetProperty("requestId").GetString()!;
                current = Hold("held", generation: 5, revision: 2, requestId: request, mode: mode,
                    work: [HeldWork("workflow", "run-created", executing: false, requiresReview: false)]);
                return current;
            }
            using var plugin = Create(host);
            var panel = plugin.ConfigurationPanels.Single(item => item.Id == "work-hold").CreateView();
            var action = Find<Button>(panel, automationId);
            var summary = Find<TextBlock>(panel, "ResourcePause.WorkHoldSummary");
            PumpUntil(() => action.IsEnabled); action.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            PumpUntil(() => summary.Text.Contains("engine admission is held", StringComparison.OrdinalIgnoreCase));
            Check(Guid.TryParseExact(admitted.GetProperty("requestId").GetString(), "N", out _) &&
                  admitted.GetProperty("mode").GetString() == mode && admitted.GetProperty("expectedEpoch").GetInt64() == 41 &&
                  admitted.GetProperty("expectedGeneration").GetInt64() == 4 && admitted.GetProperty("expectedRevision").GetInt64() == 9,
                mode + " creates one exact owner request and accepts the next hold generation's independent revision");
            Check(host.Operations.Count(operation => operation == "engine.work-hold") == 1 &&
                  !host.Operations.Any(operation => operation.Contains("cancel", StringComparison.Ordinal)),
                mode + " is dispatched once and never implies cancellation by merely rendering or refreshing the hold");
        }
    }

    private static void WorkHoldResolutionUsesExactCas()
    {
        foreach (var sample in new[]
        {
            (Cancel: false, AutomationId: "ResourcePause.WorkHoldResume", Operation: "engine.work-hold.resume", Final: "resumed"),
            (Cancel: true, AutomationId: "ResourcePause.WorkHoldCancel", Operation: "engine.work-hold.cancel", Final: "cancelled")
        })
        {
            var current = Hold("held", generation: 8, revision: 15,
                work: [HeldWork("workflow", "run-resolve", executing: false, requiresReview: true)]);
            JsonElement admitted = default;
            var host = new FixtureHost
            {
                Reply = (operation, arguments, _) => Task.FromResult(operation switch
                {
                    "engine.work-hold.status" => current,
                    var value when value == sample.Operation => Resolve(arguments),
                    _ => throw new InvalidOperationException("Unexpected fixture operation " + operation)
                })
            };
            DesktopServiceCommandResult Resolve(object arguments)
            {
                admitted = JsonSerializer.SerializeToElement(arguments);
                current = Hold(sample.Final, generation: 8, revision: 17, work: [], enginePaused: sample.Cancel);
                return current;
            }
            using var plugin = Create(host);
            var panel = plugin.ConfigurationPanels.Single(item => item.Id == "work-hold").CreateView();
            var action = Find<Button>(panel, sample.AutomationId);
            var summary = Find<TextBlock>(panel, "ResourcePause.WorkHoldSummary");
            PumpUntil(() => action.IsEnabled); action.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            PumpUntil(() => summary.Text.StartsWith("Pause new engine work", StringComparison.Ordinal));
            Check(admitted.GetProperty("holdId").GetString() == HoldId && admitted.GetProperty("expectedEpoch").GetInt64() == 41 &&
                  admitted.GetProperty("expectedGeneration").GetInt64() == 8 && admitted.GetProperty("expectedRevision").GetInt64() == 15,
                sample.Operation + " resolves only the exact held identity and CAS boundary");
            Check(host.Operations.Count(operation => operation == sample.Operation) == 1 &&
                  !host.Operations.Any(operation => operation is "jobs.workflow.submit" or "jobs.workflow.retry"),
                sample.Operation + " does not replay or submit captured work from the plugin");
        }

        var executing = Hold("held", generation: 9, revision: 20,
            work: [HeldWork("workflow", "run-executing", executing: true, requiresReview: false)]);
        var executingHost = new FixtureHost
        {
            Reply = (operation, _, _) => Task.FromResult(operation == "engine.work-hold.status"
                ? executing : throw new InvalidOperationException("Unexpected fixture operation " + operation))
        };
        using var executingPlugin = Create(executingHost);
        var executingPanel = executingPlugin.ConfigurationPanels.Single(item => item.Id == "work-hold").CreateView();
        var resume = Find<Button>(executingPanel, "ResourcePause.WorkHoldResume");
        var cancel = Find<Button>(executingPanel, "ResourcePause.WorkHoldCancel");
        PumpUntil(() => cancel.IsEnabled);
        Check(!resume.IsEnabled && cancel.IsEnabled,
            "executing captured work cannot be resumed, while explicit owner cancellation remains a separate available action");
    }

    private static void WorkHoldMalformedStatusFailsClosed()
    {
        var host = new FixtureHost
        {
            Reply = (operation, _, _) => Task.FromResult(operation == "engine.work-hold.status"
                ? Data(new { contract = "cerebral.owner-work-hold/1", state = "held", engineEpoch = 41, generation = 1, revision = 2,
                    startupPromptRequired = true, work = Array.Empty<object>(), detail = "malformed fixture" })
                : throw new InvalidOperationException("Unexpected fixture operation " + operation))
        };
        using var plugin = Create(host);
        var view = plugin.MountContributions.OfType<DesktopMountView>()
            .Single(item => item.Descriptor.Id == "work-hold-status").CreateView();
        var status = Find<Button>(view, "ResourcePause.WorkHoldStatus");
        PumpUntil(() => Label(status) == "Work hold attention");
        Check(Indicator(status).Fill is SolidColorBrush brush && brush.Color == Brushes.IndianRed.Color &&
              host.Operations.SequenceEqual(["engine.work-hold.status"]),
            "malformed startup hold evidence fails closed as attention without dispatching a mutation");

        var faulted = Hold("faulted", generation: 2, revision: 3,
            work: [HeldWork("workflow", "run-faulted", executing: false, requiresReview: true)],
            fault: "Engine pause was not confirmed.");
        var faultHost = new FixtureHost
        {
            Reply = (operation, _, _) => Task.FromResult(operation == "engine.work-hold.status"
                ? faulted : throw new InvalidOperationException("Unexpected fixture operation " + operation))
        };
        using var faultPlugin = Create(faultHost);
        var faultView = faultPlugin.MountContributions.OfType<DesktopMountView>()
            .Single(item => item.Descriptor.Id == "work-hold-status").CreateView();
        var faultStatus = Find<Button>(faultView, "ResourcePause.WorkHoldStatus");
        PumpUntil(() => Label(faultStatus) == "Work hold attention");
        Check(AutomationProperties.GetHelpText(faultStatus).Contains("Engine pause was not confirmed.", StringComparison.Ordinal),
            "faulted work-hold status exposes the bounded Worker fault instead of hiding it behind generic detail");
    }

    private const string HoldId = "11111111111111111111111111111111";
    private const string HoldRequestId = "22222222222222222222222222222222";

    private static DesktopServiceCommandResult Hold(string state, long generation, long revision, bool startup = false,
        object[]? work = null, string? holdId = HoldId, string? requestId = HoldRequestId, string? mode = "stop-now", bool enginePaused = true,
        string? fault = null) => Data(new
    {
        contract = "cerebral.owner-work-hold/1", holdId, requestId, generation, revision, mode, state,
        engineEpoch = 41, enginePaused, startupPromptRequired = startup, work = work ?? [], fault,
        detail = state == "none" ? "No intentional work hold is active." : "Fixture intentional work hold."
    });

    private static object HeldWork(string kind, string id, bool executing, bool requiresReview) => new
    {
        kind, id, conversationId = "conversation-owned", status = executing ? "running" : "waiting", revision = (long?)3,
        effectState = requiresReview ? "Unknown" : (string?)null, isExecuting = executing, requiresReview
    };

    private static DesktopServiceCommandResult Data(object value) =>
        new(true, JsonSerializer.SerializeToElement(value, new JsonSerializerOptions(JsonSerializerDefaults.Web)));

    private static DesktopServiceCommandResult Models(params object[] models) => Data(models);
    private static object Model(string id, string name, bool usable, string state = "unloaded", string? error = null) => new
    {
        id, name, state, error,
        capabilityStatus = new[] { new { id = "text-generation", usable, evidenceBasis = "fixture", evidence = "fixture" } }
    };

    private static DesktopServiceCommandResult Engine(bool paused, long epoch, string activeSlot = "A", bool candidatePending = false, bool alternateAvailable = true) => Data(new
    {
        paused, epoch, activeEvaluations = 0, committedDecisions = 2, candidatePending,
        active = new { slot = activeSlot, id = "engine-" + activeSlot.ToLowerInvariant() }, knownGoodSlot = "A", error = (string?)null,
        slots = new[]
        {
            new { slot = "A", available = activeSlot == "A" || alternateAvailable, buildAllowed = activeSlot != "A" && alternateAvailable, package = new { slot = "A", id = "engine-a" }, error = (string?)null },
            new { slot = "B", available = activeSlot == "B" || alternateAvailable, buildAllowed = activeSlot != "B" && alternateAvailable, package = new { slot = "B", id = "engine-b" }, error = (string?)null }
        }, unreleasedContexts = 0, recovery = (string?)null
    });

    private static DesktopServiceCommandResult WorkflowPage(object[] rows, long? next = null) => Data(new { runs = rows, nextAfterRow = next });
    private static DesktopServiceCommandResult LegacyPage(object[] rows, long? next = null) => Data(new { jobs = rows, nextAfterRow = next });

    private static T Find<T>(DependencyObject root, string automationId) where T : DependencyObject
    {
        if (root is T match && AutomationProperties.GetAutomationId(match) == automationId) return match;
        switch (root)
        {
            case Panel panel:
                foreach (UIElement child in panel.Children)
                    if (TryFind(child, automationId, out T? panelFound)) return panelFound!;
                break;
            case ContentControl content when content.Content is DependencyObject child:
                if (TryFind(child, automationId, out T? contentFound)) return contentFound!;
                break;
            case ScrollViewer scroll when scroll.Content is DependencyObject child:
                if (TryFind(child, automationId, out T? scrollFound)) return scrollFound!;
                break;
        }
        throw new KeyNotFoundException(automationId);
    }

    private static bool TryFind<T>(DependencyObject root, string automationId, out T? found) where T : DependencyObject
    {
        try { found = Find<T>(root, automationId); return true; }
        catch (KeyNotFoundException) { found = null; return false; }
    }
}
