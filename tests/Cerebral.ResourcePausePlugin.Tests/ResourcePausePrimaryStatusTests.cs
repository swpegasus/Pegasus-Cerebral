using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using Cerebral.Desktop.Plugins;

namespace Cerebral.ResourcePausePlugin.Tests;

public static partial class Program
{
    private static void RunPrimaryStatusChecks()
    {
        PrimaryEngineSwitchResumesOnceAndWaitsForConfirmation();
        PrimaryEngineMissingAlternateIsReadOnly();
        PrimaryEnginePreservesIntentionalHold();
        PrimaryHoldDoesNotQueueDuplicateToggle();
        PrimaryHoldRequiresReviewBeforeResume();
        RunWorkHoldStatusRefreshChecks();
    }

    private static void PrimaryStatusMountsRemainExactlyThree()
    {
        var host = new FixtureHost { Reply = (operation, _, _) => Task.FromResult(operation switch
        {
            "resources.local.status" => Status("active"),
            "settings.get" => LocalDefaults(),
            "engine.status" => Engine(false, 41),
            "engine.work-hold.status" => Hold("none", 0, 0, holdId: null, requestId: null, mode: null, enginePaused: false),
            _ => throw new InvalidOperationException(operation)
        }) };
        using var plugin = Create(host);
        var mounts = plugin.MountContributions.OfType<DesktopMountView>()
            .Where(view => view.Descriptor.Slot == DesktopMountSlots.StatusBar).ToArray();
        Check(mounts.Select(view => view.Descriptor.Id).Order(StringComparer.Ordinal)
            .SequenceEqual(new[] { "engine-status", "resource-status", "work-hold-status" }),
            "the package contributes exactly the existing three primary status controls");
        foreach (var mount in mounts)
        {
            var view = mount.CreateView();
            var automationId = mount.Descriptor.Id switch
            {
                "resource-status" => "ResourcePause.StatusToggle",
                "engine-status" => "ResourcePause.EngineStatusToggle",
                "work-hold-status" => "ResourcePause.WorkHoldStatus",
                _ => throw new InvalidOperationException(mount.Descriptor.Id)
            };
            Check(mount.Descriptor.Applicability?.Surfaces == DesktopMountSurfaces.Orchestrator &&
                Find<Button>(view, automationId) is not null,
                mount.Descriptor.Id + " contains its expected Orchestrator primary button");
        }
        Check(host.Operations.All(operation => operation is "resources.local.status" or "settings.get" or "engine.status" or "engine.work-hold.status"),
            "creating the three primary controls invokes only fixture read operations");
    }

    private static Button PrimaryEngine(ResourcePausePlugin plugin) => Find<Button>(
        plugin.MountContributions.OfType<DesktopMountView>().Single(view => view.Descriptor.Id == "engine-status").CreateView(),
        "ResourcePause.EngineStatusToggle");

    private static void PrimaryHoldRequiresReviewBeforeResume()
    {
        var held = Hold("held", 6, 12, startup: true, work: [HeldWork("workflow", "unknown-effect", false, true)]);
        var host = new FixtureHost { Reply = (operation, _, _) => Task.FromResult(operation == "engine.work-hold.status"
            ? held : throw new InvalidOperationException(operation)) };
        using var plugin = Create(host);
        var status = Find<Button>(plugin.MountContributions.OfType<DesktopMountView>().Single(view => view.Descriptor.Id == "work-hold-status").CreateView(), "ResourcePause.WorkHoldStatus");
        PumpUntil(() => Label(status) == "Held work needs review"); status.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(host.Operations.All(operation => operation == "engine.work-hold.status") && status.ToolTip is ToolTip balloon &&
              balloon.Content?.ToString()?.Contains("Review captured work", StringComparison.Ordinal) == true,
            "the primary hold never resumes captured uncertain work implicitly; it directs the owner to review");
    }

    private static void PrimaryEngineSwitchResumesOnceAndWaitsForConfirmation()
    {
        var paused = false; var slot = "A"; var epoch = 41L; var pending = false;
        var pauseReply = new TaskCompletionSource<DesktopServiceCommandResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var host = new FixtureHost
        {
            Reply = (operation, _, _) => operation switch
            {
                "engine.status" => Task.FromResult(Engine(paused, epoch, slot, pending)),
                "engine.work-hold.status" => Task.FromResult(Hold("none", 0, 0, holdId: null, requestId: null, mode: null, enginePaused: false)),
                "engine.pause" => pauseReply.Task,
                "jobs.workflow.active-summaries" => Task.FromResult(WorkflowPage([])),
                "queue.active-summaries" => Task.FromResult(LegacyPage([])),
                "engine.activate" => Task.FromResult(Engine(paused = true, epoch = 42, slot = "B", pending = true)),
                "engine.resume" => Task.FromResult(Engine(paused = false, epoch, slot, pending)),
                _ => throw new InvalidOperationException(operation)
            }
        };
        using var plugin = Create(host);
        var status = PrimaryEngine(plugin);
        PumpUntil(() => Label(status) == "Engine A active");
        status.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        PumpUntil(() => host.Operations.Contains("engine.pause"));
        status.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(Indicator(status).Fill is SolidColorBrush waiting && waiting.Color == Brushes.Gold.Color &&
              host.Operations.Count(operation => operation == "engine.pause") == 1,
            "the primary A/B control turns yellow and suppresses a second click while pausing");
        paused = true; pauseReply.SetResult(Engine(true, 41));
        PumpUntil(() => Label(status) == "Engine B review");
        var mutations = host.Calls.Where(call => call.Operation is "engine.pause" or "engine.activate" or "engine.resume").ToArray();
        Check(mutations.Select(call => call.Operation).SequenceEqual(["engine.pause", "engine.activate", "engine.resume"]) &&
              mutations[0].Arguments.GetProperty("expectedEpoch").GetInt64() == 41 &&
              mutations[1].Arguments.GetProperty("expectedEpoch").GetInt64() == 41 &&
              mutations[1].Arguments.GetProperty("slot").GetString() == "B" &&
              mutations[2].Arguments.GetProperty("expectedEpoch").GetInt64() == 42,
            "one primary action uses pause/drain/alternate activation/resume with exact epochs");
        Check(!paused && Indicator(status).Fill is SolidColorBrush candidate && candidate.Color == Brushes.Gold.Color,
            "a resumed but unconfirmed candidate stays yellow instead of implying readiness");
        pending = false;
        var panel = plugin.ConfigurationPanels.Single(item => item.Id == "engine-control").CreateView();
        PumpUntil(() => Label(status) == "Engine B active");
        Check(Indicator(status).Fill is SolidColorBrush confirmed && confirmed.Color == Brushes.LimeGreen.Color &&
              AutomationProperties.GetName(status).Contains("Ready", StringComparison.Ordinal),
            "only a later healthy confirmed alternate projection turns the primary control green");
    }

    private static void PrimaryEngineMissingAlternateIsReadOnly()
    {
        var host = new FixtureHost { Reply = (operation, _, _) => Task.FromResult(operation == "engine.status"
            ? Engine(false, 41, alternateAvailable: false) : throw new InvalidOperationException(operation)) };
        using var plugin = Create(host);
        var status = PrimaryEngine(plugin); PumpUntil(() => Label(status) == "Engine A active");
        status.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(host.Operations.All(operation => operation == "engine.status") && status.ToolTip is ToolTip balloon &&
              balloon.Content?.ToString() == "Engine B is unavailable." && status.IsEnabled,
            "a missing alternate shows a concise balloon without pausing or changing the current engine");
        status.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
        Check(status.ToolTip is not ToolTip, "unloading a primary status control closes its temporary balloon");
    }

    private static void PrimaryEnginePreservesIntentionalHold()
    {
        var host = new FixtureHost { Reply = (operation, _, _) => Task.FromResult(operation switch
        {
            "engine.status" => Engine(true, 41),
            "engine.work-hold.status" => Hold("held", 6, 12, work: [HeldWork("workflow", "held-run", false, false)]),
            _ => throw new InvalidOperationException(operation)
        }) };
        using var plugin = Create(host);
        var status = PrimaryEngine(plugin); PumpUntil(() => Label(status) == "Engine A paused");
        status.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        PumpUntil(() => Label(status) == "Engine attention");
        Check(!host.Operations.Any(operation => operation is "engine.pause" or "engine.activate" or "engine.resume" or "engine.work-hold.resume") &&
              AutomationProperties.GetHelpText(status).Contains("captured work hold", StringComparison.Ordinal),
            "A/B switching cannot release or mutate an intentional captured-work hold");
    }

    private static void PrimaryHoldDoesNotQueueDuplicateToggle()
    {
        var current = Hold("none", 4, 9, holdId: null, requestId: null, mode: null, enginePaused: false);
        var holdReply = new TaskCompletionSource<DesktopServiceCommandResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        JsonElement request = default;
        var host = new FixtureHost { Reply = (operation, arguments, _) => operation switch
        {
            "engine.work-hold.status" => Task.FromResult(current),
            "engine.work-hold" => Admit(arguments),
            _ => throw new InvalidOperationException(operation)
        } };
        Task<DesktopServiceCommandResult> Admit(object arguments) { request = JsonSerializer.SerializeToElement(arguments); return holdReply.Task; }
        using var plugin = Create(host);
        var status = Find<Button>(plugin.MountContributions.OfType<DesktopMountView>().Single(view => view.Descriptor.Id == "work-hold-status").CreateView(), "ResourcePause.WorkHoldStatus");
        PumpUntil(() => Label(status) == "Hold all work");
        status.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        PumpUntil(() => host.Operations.Contains("engine.work-hold"));
        status.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        current = Hold("held", 5, 2, requestId: request.GetProperty("requestId").GetString(), work: [HeldWork("workflow", "captured-run", false, false)]);
        holdReply.SetResult(current); PumpUntil(() => Label(status) == "Captured work held");
        Check(host.Operations.Count(operation => operation == "engine.work-hold") == 1 &&
              !host.Operations.Contains("engine.work-hold.resume") && request.GetProperty("mode").GetString() == "stop-now" &&
              request.GetProperty("expectedEpoch").GetInt64() == 41 && request.GetProperty("expectedGeneration").GetInt64() == 4 &&
              request.GetProperty("expectedRevision").GetInt64() == 9,
            "the primary hold uses exact saved state and a repeated click cannot queue a later automatic resume");
        Check(AutomationProperties.GetHelpText(status).Contains("remote", StringComparison.OrdinalIgnoreCase),
            "a captured-work marker explicitly leaves remote shutdown confirmation separate");
    }
}
