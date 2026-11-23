using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Cerebral.Contracts;
using Cerebral.Contracts.Workflows;
using Cerebral.Core;

namespace Cerebral.Desktop;

/// <summary>Real offscreen WPF clocks and lifecycle driven by deterministic accepted events.
/// No provider, Source, network, hardware measurement or system preference change.</summary>
public static class WorkflowActivityAnimationChecks
{
    public static async Task<int> VerifyAsync(string? imagePath = null)
    {
        var checks = 0;
        void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); checks++; }
        Check(WorkflowWorkspace.ActivityAnimationPermitted(true, false, false), "Normal system animation policy permits the bounded caption effect.");
        Check(!WorkflowWorkspace.ActivityAnimationPermitted(false, false, false), "Disabled Windows client animation keeps the step static.");
        Check(!WorkflowWorkspace.ActivityAnimationPermitted(true, true, false), "High contrast keeps the step static.");
        Check(!WorkflowWorkspace.ActivityAnimationPermitted(true, false, true), "Reduced motion keeps the step static.");

        var now = DateTimeOffset.UtcNow;
        long sequence = 1;
        var events = new List<CerebralEvent>
        {
            new("animation-original", "animation-chat", "animation-run", sequence++, CerebralEventTypes.Message,
                new(EventActorKind.User), [new TextContentPart("Read the selected page.")],
                new(ModelInclusion.Required, HumanVisibility.Transcript), new(EventSourceKind.User), now)
        };
        CerebralEvent Call(string id) => new(id, "animation-chat", "animation-run", sequence++, CerebralEventTypes.ToolCall,
            new(EventActorKind.Agent, "lead", "Cerebral"), [], new(ModelInclusion.Never, HumanVisibility.Activity),
            new(EventSourceKind.Service, "cerebral.engine"), now,
            CerebralJson.Element(new ToolCallData(id, "cerebral.host:tool", CerebralJson.Element(new
                { name = "file.read", arguments = new { path = "Example/page.html" } }))));
        events.Add(Call("old-unmatched-effect")); events.Add(Call("active-effect"));
        var saved = new WorkflowRun
        {
            Id = "animation-run", Revision = 1, Status = WorkflowStatus.Running,
            Submission = new("animation-run", "animation-chat", "lead", "fixture:animation", "1.0.0",
                "Read the selected page.", "PRIVATE ASSIGNMENT AND REASONING", WorkflowContract.EmptyObject, WorkflowContract.EmptyObject),
            Effect = new("active-effect", "cerebral.host:tool", 1, WorkflowContract.EmptyObject,
                WorkflowReplayPolicy.Never, WorkflowEffectState.Dispatched), CreatedAt = now, UpdatedAt = now
        };
        Task<CommandResult> Dispatch(CommandRequest request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            return Task.FromResult(request.Operation switch
            {
                "jobs.workflow.get" => CommandResult.Ok(saved),
                "jobs.workflow.events.previous" => CommandResult.Ok(new { events, snapshotSequence = events[^1].Sequence, nextBeforeSequence = (long?)null }),
                "jobs.workflow.events" => CommandResult.Ok(new { events = new[] { events[0] }, snapshotSequence = events[^1].Sequence, nextAfterSequence = (long?)null }),
                _ => throw new InvalidOperationException("Unexpected animation fixture operation: " + request.Operation)
            });
        }
        using var workspace = new WorkflowWorkspace(Dispatch, () => [], (_, _) => { }, embedded: true);
        var policy = typeof(WorkflowWorkspace).GetField("activityAnimationMotionPolicyForChecks", BindingFlags.Instance | BindingFlags.NonPublic)!;
        policy.SetValue(workspace, (Func<bool>)(() => true));
        workspace.BeginConversation("animation-chat"); await workspace.OpenRunSnapshotAsync("animation-run");
        var frame = new Border { Background = Ui.Surface, Padding = new Thickness(14), Child = workspace };
        using var source = new HwndSource(new HwndSourceParameters("Cerebral activity animation fixture")
            { Width = 740, Height = 360, WindowStyle = unchecked((int)0x80000000), PositionX = -32000, PositionY = -32000 }) { RootVisual = frame };
        source.CompositionTarget.RenderMode = RenderMode.SoftwareOnly;
        async Task Flush()
        {
            await workspace.Dispatcher.InvokeAsync(() => { frame.Measure(new Size(740, 360)); frame.Arrange(new Rect(0, 0, 740, 360)); frame.UpdateLayout(); }, DispatcherPriority.Loaded);
            await workspace.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
        }
        Expander Row(string id) => Descendants(workspace).OfType<Expander>().Single(item =>
            AutomationProperties.GetAutomationId(item) == "WorkflowActivityEntry-" + id);
        TextBlock Caption(Expander row) => Descendants(row).OfType<TextBlock>().Single(item =>
            AutomationProperties.GetAutomationId(item) == "WorkflowActivityStepText");
        TextBlock Marker(Expander row) => Descendants(row).OfType<TextBlock>().Single(item =>
            AutomationProperties.GetAutomationId(item) == "WorkflowActivityStepMarker");
        static bool Animated(TextBlock caption) => caption.Foreground.HasAnimatedProperties;
        await Flush();
        var active = Row("active-effect"); var caption = Caption(active); var marker = Marker(active);
        var publicText = caption.Text;
        Check(Animated(caption) && Animated(marker) && marker.Text == "●", "Exact current dispatched step and its small marker own a real WPF animation clock.");
        Check(!Animated(Caption(Row("old-unmatched-effect"))), "Another pending call in the same turn cannot animate as the current effect.");
        Check(publicText.Contains("Reading", StringComparison.Ordinal) && publicText.Contains("page.html", StringComparison.Ordinal) &&
            !publicText.Contains("PRIVATE", StringComparison.Ordinal), "Animation uses the recorded public operation caption without assignment or reasoning text.");
        if (imagePath is not null)
        {
            // The offscreen HWND must compose the binding update produced during Loaded.
            await Task.Delay(80); await Flush();
            var currentMarker = Marker(Row("active-effect"));
            if (currentMarker.Text != "●" || !Animated(currentMarker) || !Animated(Caption(Row("active-effect"))))
                throw new InvalidOperationException("The optional figure must capture the actual confirmed active marker and caption.");
            var bitmap = new RenderTargetBitmap(740, 360, 96, 96, PixelFormats.Pbgra32); bitmap.Render(frame);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var file = new FileStream(imagePath, FileMode.CreateNew, FileAccess.Write, FileShare.Read); encoder.Save(file);
        }

        var model = new WorkflowWorkspace.ActivityRow("active-effect", events[^1]);
        var run = workspace.Snapshot!.Run!;
        Check(WorkflowWorkspace.IsCurrentAnimatedActivityStep(model, run), "The exact current accepted dispatch is eligible.");
        foreach (var status in new[] { WorkflowStatus.Waiting, WorkflowStatus.Completed, WorkflowStatus.Failed, WorkflowStatus.Cancelled, WorkflowStatus.Queued })
            Check(!WorkflowWorkspace.IsCurrentAnimatedActivityStep(model, run with { Status = status }), "Nonexecuting state " + status + " cannot animate.");
        Check(!WorkflowWorkspace.IsCurrentAnimatedActivityStep(model, run with { CancelRequested = true }), "Cancellation requested cannot animate as confirmed working.");
        Check(!WorkflowWorkspace.IsCurrentAnimatedActivityStep(model, run with { EffectState = WorkflowEffectState.Unknown }), "Unknown effect cannot animate as confirmed working.");
        Check(!WorkflowWorkspace.IsCurrentAnimatedActivityStep(model, run with { EffectId = "another-effect" }), "An effect identity mismatch cannot animate.");

        policy.SetValue(workspace, (Func<bool>)(() => false));
        await Task.Delay(250); await Flush();
        Check(!Animated(caption) && !Animated(marker) && caption.Text.Contains("Reading", StringComparison.Ordinal) &&
            caption.Text.Contains("page.html", StringComparison.Ordinal) && marker.Text == "●",
            "Reduced animation leaves the exact public text and informative marker intact.");
        policy.SetValue(workspace, (Func<bool>)(() => true));
        typeof(WorkflowWorkspace).GetMethod("RefreshActivityAnimations", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(workspace, []);
        Check(Animated(caption), "Re-enabling fixture motion starts the same caption rather than replacing its control.");
        var activity = Descendants(workspace).OfType<Expander>().Single(item => AutomationProperties.GetAutomationId(item) == "WorkflowActivity");
        activity.IsExpanded = false; await Flush();
        Check(!Animated(caption), "Collapsed activity does not keep a hidden animation clock running.");
        activity.IsExpanded = true; await Flush();
        Check(ReferenceEquals(Caption(Row("active-effect")), caption) && Animated(caption), "Showing active activity resumes the same confirmed step control.");

        workspace.ResetGenerationPreviewFeed(); await Task.Delay(250); await Flush();
        Check(!Animated(caption), "Feed reset stops prior binding animation without inventing new work.");
        // A fresh deterministic journal change causes the normal reconciliation hook to bind again.
        events.Add(new("fixture-fresh-dispatch", "animation-chat", "animation-run", sequence++, CerebralEventTypes.WorkflowTransition,
            new(EventActorKind.Service, "cerebral.engine"), [], new(ModelInclusion.Never, HumanVisibility.Activity),
            new(EventSourceKind.Service, "cerebral.engine", JobId: "animation-run"), now,
            CerebralJson.Element(new { runId = "animation-run", status = "Running", reason = "effect.dispatched" })));
        await workspace.InspectRunAsync("animation-run"); await Flush();
        caption = Caption(Row("active-effect"));
        Check(Animated(caption), "Fresh correlated journal reconciliation can animate after feed reset.");

        saved = saved with { Status = WorkflowStatus.Waiting, Revision = saved.Revision + 1, WaitingReason = "Waiting for the fixture owner." };
        await workspace.InspectRunAsync("animation-run"); await Flush();
        Check(!Animated(Caption(Row("active-effect"))), "A real rendered wait transition stops animation.");
        saved = saved with { Status = WorkflowStatus.Running, Revision = saved.Revision + 1, WaitingReason = null };
        await workspace.InspectRunAsync("animation-run"); await Flush();
        caption = Caption(Row("active-effect")); Check(Animated(caption), "The exact rendered current dispatch can resume after waiting.");
        workspace.BeginConversation("animation-other"); await Task.Delay(250); await Flush();
        Check(!Animated(caption), "Navigation cannot leave the previous conversation's animation running.");
        workspace.BeginConversation("animation-chat"); await workspace.OpenRunSnapshotAsync("animation-run"); await Flush();
        caption = Caption(Row("active-effect")); Check(Animated(caption), "Selecting the confirmed current run reconstructs only its own step animation.");
        frame.Child = null; await Flush();
        Check(!Animated(caption), "Unloading stops the native caption clock.");
        frame.Child = workspace; await Flush();
        caption = Caption(Row("active-effect"));
        Check(Animated(caption), "A reloaded current step has a bounded animation without duplicate control ownership.");
        workspace.Dispose();
        Check(!Animated(caption), "Disposal cancels animation immediately through the existing workspace lifetime.");
        return checks;
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        yield return root;
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
            foreach (var child in Descendants(VisualTreeHelper.GetChild(root, index))) yield return child;
    }
}
