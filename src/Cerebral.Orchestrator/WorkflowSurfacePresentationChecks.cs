using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Cerebral.Contracts;
using Cerebral.Contracts.Workflows;
using Cerebral.Core;
using Cerebral.Desktop;

namespace Cerebral.Orchestrator;

public sealed partial class OrchestratorWindow
{
    /// <summary>
    /// Exercises the shown production shell, themed item templates and in-process automation
    /// peers with one deterministic saved run. The temporary Work surface has no service client.
    /// </summary>
    internal async Task<int> VerifyWorkflowSurfacePresentationAsync(CancellationToken cancellationToken = default)
    {
        Dispatcher.VerifyAccess();
        if (!IsLoaded || !IsVisible || new WindowInteropHelper(this).Handle == IntPtr.Zero ||
            SynchronizationContext.Current is not DispatcherSynchronizationContext)
            throw new InvalidOperationException("Run presentation checks on the active dispatcher of the shown task-owned Orchestrator.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, Lifetime.Token);
        deadline.CancelAfter(TimeSpan.FromSeconds(12));
        var token = deadline.Token;
        var checks = 0;
        void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
            checks++;
        }

        const string runId = "presentation-fixture-run";
        const string conversationId = "presentation-fixture-conversation";
        const string profileId = "presentation-fixture-profile";
        const string title = "Review the saved presentation task.";
        const string profileName = "Presentation helper";
        const string failure = "Fixture validation failed; review the saved result.";
        const string expectedLabel = title + "\n" + profileName + " · Failed";
        var now = DateTimeOffset.UnixEpoch;
        var run = new WorkflowRun
        {
            Id = runId, Revision = 1, Status = WorkflowStatus.Failed, Error = failure,
            Submission = new(runId, conversationId, profileId, "fixture.presentation", "1.0.0",
                title, null, WorkflowContract.EmptyObject, WorkflowContract.EmptyObject),
            CreatedAt = now, UpdatedAt = now
        };
        var original = new CerebralEvent("presentation-fixture-original", conversationId, runId, 1,
            CerebralEventTypes.Message, new(EventActorKind.User), [new TextContentPart(title)],
            new(ModelInclusion.Required, HumanVisibility.Transcript), new(EventSourceKind.User), now);
        var calls = new List<string>();
        var reports = new List<(string Message, bool Error)>();
        Task<CommandResult> Dispatch(CommandRequest request, CancellationToken requestToken)
        {
            requestToken.ThrowIfCancellationRequested();
            Dispatcher.VerifyAccess();
            if (request.ProfileId is not null) throw new InvalidOperationException("The presentation fixture accepts owner reads only.");
            calls.Add(request.Operation);
            switch (request.Operation)
            {
                case "jobs.workflow.recent":
                    return Task.FromResult(CommandResult.Ok(new WorkflowRecentPage(
                        [new(runId, conversationId, profileId, title, "fixture.presentation", WorkflowStatus.Failed, 1, now)], 1, null)));
                case "jobs.workflow.get":
                    if (request.Arguments.GetProperty("id").GetString() != runId)
                        throw new InvalidOperationException("The presentation fixture refuses a foreign run read.");
                    return Task.FromResult(CommandResult.Ok(run));
                case "jobs.workflow.events.previous":
                    if (request.Arguments.GetProperty("id").GetString() != runId)
                        throw new InvalidOperationException("The presentation fixture refuses a foreign event read.");
                    return Task.FromResult(CommandResult.Ok(new
                        { events = new[] { original }, snapshotSequence = 1, nextBeforeSequence = (long?)null }));
                default:
                    throw new InvalidOperationException("The presentation fixture refuses operation: " + request.Operation);
            }
        }

        using var fixture = new WorkflowWorkspace(Dispatch,
            () => new[] { new AgentProfile { Id = profileId, Name = profileName, Role = "helper" } },
            (message, error) => reports.Add((message, error)));
        var selector = new WorkflowSurfaceTemplateSelector();
        var selectorList = new ListBox
        {
            ItemsSource = new[] { new WorkflowSurfaceSelectorValue("Friendly selector label") },
            ItemTemplateSelector = selector,
            Margin = new Thickness(0, 8, 0, 0)
        };
        AutomationProperties.SetAutomationId(selectorList, "WorkflowSurfaceSelectorControl");
        var surface = new Grid();
        surface.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        surface.RowDefinitions.Add(new() { Height = new GridLength(64) });
        surface.Children.Add(fixture); Grid.SetRow(selectorList, 1); surface.Children.Add(selectorList);
        var work = pages["work"];
        var originalContent = work.Content;
        var originalSelection = Tabs.SelectedItem;
        var originalWorkspace = workflowWorkspace;
        try
        {
            workflowWorkspace = fixture;
            SetPageContent("work", surface);
            SelectPage("work");
            await FlushWorkflowSurfaceAsync(token);
            await fixture.RefreshAsync().WaitAsync(token);
            await FlushWorkflowSurfaceAsync(token);
            var recent = WorkflowSurfaceVisuals(surface).OfType<ListBox>().Single(item =>
                AutomationProperties.GetAutomationId(item) == "WorkflowRecent");
            if (recent.Items.Count != 1) throw new InvalidOperationException("The shown recent-work fixture did not load its exact saved row.");
            recent.ScrollIntoView(recent.Items[0]);
            await FlushWorkflowSurfaceAsync(token);
            var row = recent.ItemContainerGenerator.ContainerFromIndex(0) as ListBoxItem
                ?? throw new InvalidOperationException("The shown recent-work row was not realized.");
            var rendered = WorkflowSurfaceVisuals(row).OfType<TextBlock>().Select(block => block.Text).ToArray();
            Check(recent.IsVisible && recent.ActualWidth > 0 && recent.ActualHeight > 0 && recent.Items.Count == 1,
                "The actual selected Work surface realizes its recent-run list with positive bounds.");
            Check(rendered.Contains(expectedLabel, StringComparer.Ordinal),
                "The actual themed row renders its friendly Label rather than its record representation.");
            Check(rendered.All(text => !text.Contains("WorkflowRunListItem", StringComparison.Ordinal) &&
                !text.Contains("WorkflowRunSummary", StringComparison.Ordinal) && !text.Contains("Label =", StringComparison.Ordinal)),
                "The generated row text contains no record-codec labels.");
            Check(AutomationProperties.GetAutomationId(row) == "WorkflowRun-" + runId &&
                AutomationProperties.GetName(row) == expectedLabel && AutomationProperties.GetHelpText(row) == conversationId,
                "The realized row carries the exact run ID, friendly automation name and conversation help identity.");

            var selectorRow = selectorList.ItemContainerGenerator.ContainerFromIndex(0) as ListBoxItem
                ?? throw new InvalidOperationException("The selector control row was not realized.");
            var selectorText = WorkflowSurfaceVisuals(selectorRow).OfType<TextBlock>().Select(block => block.Text).ToArray();
            Check(selector.Calls > 0 && selectorText.Contains(WorkflowSurfaceTemplateSelector.ExpectedText, StringComparer.Ordinal) &&
                selectorText.All(text => !text.Contains("SELECTOR RECORD MUST NOT RENDER", StringComparison.Ordinal)),
                "The same shared themed item template preserves an explicit ContentTemplateSelector's generated content.");

            var peers = WorkflowSurfacePeers();
            var recentPeer = peers.SingleOrDefault(peer => peer.GetAutomationId() == "WorkflowRecent");
            var recentBounds = recentPeer?.GetBoundingRectangle() ?? Rect.Empty;
            Check(recentPeer is not null && recentPeer.GetAutomationControlType() == AutomationControlType.List &&
                !recentPeer.IsOffscreen() && recentBounds.Width > 0 && recentBounds.Height > 0,
                "The actual WindowAutomationPeer subtree exposes the selected headerless tab's recent-work list.");
            var rowPeer = peers.SingleOrDefault(peer => peer.GetAutomationId() == "WorkflowRun-" + runId);
            var rowBounds = rowPeer?.GetBoundingRectangle() ?? Rect.Empty;
            Check(rowPeer is not null && rowPeer.GetName() == expectedLabel && rowPeer.GetHelpText() == conversationId &&
                rowPeer.GetAutomationControlType() == AutomationControlType.ListItem && !rowPeer.IsOffscreen() &&
                rowBounds.Width > 0 && rowBounds.Height > 0,
                "The actual window peer subtree exposes the exact native row with its friendly name and conversation identity.");
            var selection = rowPeer?.GetPattern(PatternInterface.SelectionItem) as ISelectionItemProvider
                ?? throw new InvalidOperationException("The recent-run automation peer has no native selection pattern.");
            selection.Select();
            while (fixture.Snapshot?.RunId != runId) await Task.Delay(10, token);
            await FlushWorkflowSurfaceAsync(token);
            Check(fixture.Snapshot is { ConversationId: conversationId, RunId: runId, Run.Status: WorkflowStatus.Failed } &&
                recent.SelectedIndex == 0,
                "Selecting the native row adopts that exact saved failed run through the read-only workflow client.");

            peers = WorkflowSurfacePeers();
            var selectedPeer = peers.SingleOrDefault(peer => peer.GetAutomationId() == "WorkflowSelectedRun-" + runId);
            var selectedBounds = selectedPeer?.GetBoundingRectangle() ?? Rect.Empty;
            Check(selectedPeer is not null && selectedPeer.GetName() == title && selectedPeer.GetHelpText() == conversationId &&
                !selectedPeer.IsOffscreen() && selectedBounds.Width > 0 && selectedBounds.Height > 0,
                "A visible selected-run heading exposes its exact run/conversation identity with a friendly name.");
            var heading = WorkflowSurfaceVisuals(surface).OfType<TextBlock>().Single(block =>
                AutomationProperties.GetAutomationId(block) == "WorkflowSelectedRun-" + runId);
            var state = WorkflowSurfaceVisuals(surface).OfType<TextBlock>().Single(block =>
                AutomationProperties.GetAutomationId(block) == "WorkflowState");
            Check(heading.Text == title && !heading.Text.Contains(runId, StringComparison.Ordinal) &&
                !heading.Text.Contains(conversationId, StringComparison.Ordinal),
                "Selected-run identity metadata does not introduce raw IDs into the visible heading.");
            Check(state.Text == failure && ReferenceEquals(state.Foreground, Ui.ErrorText),
                "The selected failed run presents its real error text and failure foreground.");
            var recentReads = calls.Count(operation => operation == "jobs.workflow.recent");
            await fixture.RefreshAsync().WaitAsync(token);
            await FlushWorkflowSurfaceAsync(token);
            Check(calls.Count(operation => operation == "jobs.workflow.recent") > recentReads && state.Text == failure &&
                ReferenceEquals(state.Foreground, Ui.ErrorText) &&
                AutomationProperties.GetAutomationId(heading) == "WorkflowSelectedRun-" + runId,
                "A successful recent-list refresh preserves the selected run's genuine failure and identity.");
            row = recent.ItemContainerGenerator.ContainerFromIndex(0) as ListBoxItem
                ?? throw new InvalidOperationException("Refreshing removed the saved native row.");
            Check(AutomationProperties.GetAutomationId(row) == "WorkflowRun-" + runId &&
                AutomationProperties.GetName(row) == expectedLabel && AutomationProperties.GetHelpText(row) == conversationId,
                "Recreating recent-list items preserves stable run and conversation automation identities.");
            Check(calls.Contains("jobs.workflow.get") && calls.Contains("jobs.workflow.events.previous") &&
                calls.All(operation => operation is "jobs.workflow.recent" or "jobs.workflow.get" or "jobs.workflow.events.previous") &&
                reports.All(report => !report.Error),
                "Presentation and native selection use only the strict in-memory saved-run/event reads with no mutation or provider command.");
        }
        finally
        {
            workflowWorkspace = originalWorkspace;
            work.Content = originalContent;
            Tabs.SelectedItem = originalSelection;
            fixture.Dispose();
        }
        Check(ReferenceEquals(work.Content, originalContent) && ReferenceEquals(Tabs.SelectedItem, originalSelection) &&
            ReferenceEquals(workflowWorkspace, originalWorkspace),
            "The shown fixture restores the original Work surface, workspace binding and selected page.");
        Console.WriteLine($"Workflow surface presentation: {checks} deterministic shown-WPF/automation-peer checks passed; no Worker/provider/model execution.");
        return checks;
    }

    private async Task FlushWorkflowSurfaceAsync(CancellationToken token)
    {
        await Dispatcher.InvokeAsync(() => { ApplyTemplate(); UpdateLayout(); }, DispatcherPriority.Loaded, token);
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle, token);
    }

    private IReadOnlyList<AutomationPeer> WorkflowSurfacePeers()
    {
        var root = UIElementAutomationPeer.CreatePeerForElement(this)
            ?? throw new InvalidOperationException("The shown Window has no automation peer.");
        var result = new List<AutomationPeer>();
        var queue = new Queue<(AutomationPeer Peer, int Depth)>();
        var visited = new HashSet<AutomationPeer>(ReferenceEqualityComparer.Instance);
        queue.Enqueue((root, 0));
        while (queue.Count > 0)
        {
            var (peer, depth) = queue.Dequeue();
            if (!visited.Add(peer)) continue;
            if (depth > 32 || result.Count >= 2048)
                throw new InvalidOperationException("The shown fixture's automation-peer tree exceeded its bound.");
            result.Add(peer);
            foreach (var child in peer.GetChildren() ?? []) queue.Enqueue((child, depth + 1));
        }
        return result;
    }

    private static IReadOnlyList<DependencyObject> WorkflowSurfaceVisuals(DependencyObject root)
    {
        var result = new List<DependencyObject>();
        var queue = new Queue<(DependencyObject Node, int Depth)>();
        queue.Enqueue((root, 0));
        while (queue.Count > 0)
        {
            var (node, depth) = queue.Dequeue();
            if (depth > 32 || result.Count >= 2048)
                throw new InvalidOperationException("The shown fixture's visual tree exceeded its bound.");
            result.Add(node);
            for (var index = 0; index < VisualTreeHelper.GetChildrenCount(node); index++)
                queue.Enqueue((VisualTreeHelper.GetChild(node, index), depth + 1));
        }
        return result;
    }

    private sealed record WorkflowSurfaceSelectorValue(string Label)
    {
        public override string ToString() => "SELECTOR RECORD MUST NOT RENDER";
    }

    private sealed class WorkflowSurfaceTemplateSelector : DataTemplateSelector
    {
        internal const string ExpectedText = "Selected template: friendly presentation content";
        internal int Calls { get; private set; }
        public override DataTemplate SelectTemplate(object item, DependencyObject container)
        {
            // WPF can query a disconnected/null placeholder while recycling item containers.
            if (item is not WorkflowSurfaceSelectorValue) return null!;
            Calls++;
            var text = new FrameworkElementFactory(typeof(TextBlock));
            text.SetValue(TextBlock.TextProperty, ExpectedText);
            return new DataTemplate(typeof(WorkflowSurfaceSelectorValue)) { VisualTree = text };
        }
    }
}
