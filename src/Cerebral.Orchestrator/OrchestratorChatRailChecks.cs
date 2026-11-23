using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Input;
using Cerebral.Core;
using Cerebral.Desktop;

namespace Cerebral.Orchestrator;

public sealed partial class OrchestratorWindow
{
    // Runs inside the existing isolated, unshown composer-layout fixture. No owner calls; optional PNGs use only owned preview paths.
    private int CheckChatRailAndWorkPanels(FrameworkElement container, string? previewDirectory = null)
    {
        var oldNavigation = navigation; var oldSessions = navigationSessions;
        var oldSelection = selectedNavigationId; var oldFilter = navigationFilter.Text;
        var oldDraft = prompt.Text; var checks = 0;
        var oldPanelsRequested = activityRequested;
        var oldDisplayCount = sidebarRecentCount; var oldOccurrence = selectedNavigationOccurrence;
        void Check(bool value, string message)
        { if (!value) throw new InvalidOperationException(message); checks++; }
        static IEnumerable<T> Children<T>(DependencyObject view) where T : DependencyObject
        {
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(view); i++)
            {
                var child = VisualTreeHelper.GetChild(view, i);
                if (child is T match) yield return match;
                foreach (var descendant in Children<T>(child)) yield return descendant;
            }
        }
        IEnumerable<TreeViewItem> CanonicalRows(ItemsControl parent)
        {
            foreach (var row in parent.Items.OfType<TreeViewItem>())
            {
                if (AutomationProperties.GetAutomationId(row) == "NavigationSection.Recent") continue;
                yield return row;
                foreach (var child in CanonicalRows(row)) yield return child;
            }
        }
        try
        {
            sidebarRecentCount = DefaultSidebarRecentCount;
            navigationSessions = Enumerable.Range(0, 32).Select(i => new TaskSession
            {
                Id = "rail-chat-" + i, ProfileId = "lead", Title = "Chat " + i,
                ProjectId = i < 3 ? "rail-project" : null, UpdatedAt = DateTimeOffset.UtcNow.AddMinutes(-i)
            }).ToList();
            navigation = new() { Nodes = [new() { Id = "rail-project", Kind = "project", Title = "Project Alpha" }] };
            selectedNavigationId = null; navigationFilter.Clear(); RenderNavigation();
            var searchPlaceholder = Children<TextBlock>(workspaceRail!).Single(view =>
                AutomationProperties.GetAutomationId(view) == "NavigationSearchPlaceholder");
            Check(searchPlaceholder.Text == "Search chats" && searchPlaceholder.Visibility == Visibility.Visible &&
                !searchPlaceholder.IsHitTestVisible && navigationFilter.Text.Length == 0,
                "Search has a noninteractive ghost hint without putting hint text into the query.");
            container.Measure(new Size(1280, 840)); container.Arrange(new Rect(0, 0, 1280, 840)); container.UpdateLayout();
            var rows = CanonicalRows(navigationTree).Where(row => row.Tag is NavigationNode { Kind: "conversation" }).ToArray();
            var recent = (TreeViewItem)navigationTree.Items[0];
            var shortcuts = recent.Items.OfType<TreeViewItem>().ToArray();
            Check(AutomationProperties.GetAutomationId(recent) == "NavigationSection.Recent" &&
                shortcuts.Length == 5 && shortcuts.Select(row => ((NavigationNode)row.Tag).TargetId).Distinct().Count() == 5 &&
                shortcuts.All(row => rows.Any(canonical => ReferenceEquals(canonical.Tag, row.Tag))),
                "The first Recent section contains five unique references to the same canonical chat nodes.");
            var fixtureRows = rows.Where(row => row.Tag is NavigationNode { TargetId: not null } node &&
                node.TargetId.StartsWith("rail-chat-", StringComparison.Ordinal)).ToArray();
            Check(fixtureRows.Length == 32 && fixtureRows.Select(row => ((NavigationNode)row.Tag).TargetId!).ToHashSet(StringComparer.Ordinal)
                .SetEquals(navigationSessions.Select(item => item.Id)),
                "All 32 loaded chat IDs occur exactly once in the canonical folder tree.");
            Check(CanonicalRows(navigationTree).Single(row => row.Tag is NavigationNode { Id: "rail-project" }).Items.Count == 3 &&
                CanonicalRows(navigationTree).Single(row => AutomationProperties.GetAutomationId(row) == "NavigationSection.Unassigned").Items.Count >= 29,
                "Project chats retain their actual subtree and unfiled chats retain the virtual Unassigned group.");
            Check(navigationFilter.ActualHeight <= 32 && navigationTree.ActualHeight > 400,
                "Search stays compact and the chat list receives the remaining rail height.");
            Check(workspaceRail is not null && !Children<Button>(workspaceRail).Any(), "Persistent action buttons do not occupy the chat rail.");
            Check(BuildChatActionsMenu().Items.Count >= 10, "Chat and project actions remain available through the application menu.");
            navigationFilter.Text = "Project Alpha";
            Check(searchPlaceholder.Visibility == Visibility.Collapsed,
                "Typing a real query hides the search hint.");
            Check(CanonicalRows(navigationTree).Count(row => row.Tag is NavigationNode { TargetId: not null } node &&
                node.TargetId.StartsWith("rail-chat-", StringComparison.Ordinal)) == 3 &&
                CanonicalRows(navigationTree).Single(row => row.Tag is NavigationNode { Id: "rail-project" }).Items.Count == 3,
                "Searching a project finds exactly its three canonical chats in their real project subtree.");
            navigationFilter.Clear();
            Check(searchPlaceholder.Visibility == Visibility.Visible,
                "Clearing a query restores the search hint.");
            var oldDockOwner = CurrentConversationId;
            var dockDraft = prompt.Text;
            activityRequested = true; UpdateConversationLayout();
            container.Measure(new Size(1280, 840)); container.Arrange(new Rect(0, 0, 1280, 840)); container.UpdateLayout();
            var peers = new[] { mainWorkPanel!, coordinatorDockPanel!, workersDockPanel! };
            Check(ReferenceEquals(mainWorkPanel!.Parent, workPanelLayout) &&
                peers.All(panel => ReferenceEquals(panel.Parent, workPanelLayout)) &&
                AutomationProperties.GetAutomationId(workPanelLayout!) == "ConversationDockPanels" &&
                peers.Count(panel => panel.IsExpanded) == 1 && mainWorkPanel.IsExpanded,
                "Main, Coordinator and Workers share the actual center accordion with Main alone open by default.");
            System.Windows.Controls.Primitives.ToggleButton Header(Expander panel)
            {
                panel.ApplyTemplate();
                return panel.Template.FindName("DockHeaderButton", panel) as System.Windows.Controls.Primitives.ToggleButton ??
                    throw new InvalidOperationException("Missing actual accordion header.");
            }
            void ToggleHeader(Expander panel)
            {
                var peer = new System.Windows.Automation.Peers.ToggleButtonAutomationPeer(Header(panel));
                ((System.Windows.Automation.Provider.IToggleProvider)peer.GetPattern(System.Windows.Automation.Peers.PatternInterface.Toggle)).Toggle();
                container.UpdateLayout();
            }
            foreach (var (panel, label) in new[] { (mainWorkPanel!, "Main"), (coordinatorDockPanel!, "Coordinator"), (workersDockPanel!, "Workers") })
            {
                var header = Header(panel);
                Check(AutomationProperties.GetName(header) == label && header.MinHeight == 24 && header.ActualHeight is >= 24 and <= 28 &&
                    Math.Abs(header.ActualWidth - panel.ActualWidth) <= 1 &&
                    Children<TextBlock>(header).Count(text => text.Text is "▾" or "▸") == 1 &&
                    header.IsHitTestVisible && header.Focusable,
                    "The " + label + " header is a compact whole-row hit target with one disclosure marker and an accessible name.");
                ToggleHeader(panel);
                Check(panel.IsExpanded && peers.Count(item => item.IsExpanded) == 1 &&
                    prompt.Text == dockDraft && CurrentConversationId == oldDockOwner,
                    "The actual " + label + " header action selects exactly one peer and preserves chat/draft without invoking work.");
                ToggleHeader(panel);
                Check(panel.IsExpanded && Header(panel).IsChecked == true && peers.Count(item => item.IsExpanded) == 1,
                    "Clicking the already-selected " + label + " header keeps its actual selected marker and cannot leave the accordion empty.");
            }
            var workerHeader = Header(workersDockPanel!); var mainHeader = Header(mainWorkPanel);
            mainHeader.RaiseEvent(new KeyboardFocusChangedEventArgs(Keyboard.PrimaryDevice, 0, workerHeader, mainHeader)
                { RoutedEvent = Keyboard.GotKeyboardFocusEvent });
            container.UpdateLayout();
            Check(mainWorkPanel.IsExpanded && peers.Count(panel => panel.IsExpanded) == 1 && prompt.Text == dockDraft,
                "A routed keyboard-focus event from the generated header returns to Main without changing the draft.");
            if (previewDirectory is not null)
            {
                if (!System.IO.Path.IsPathFullyQualified(previewDirectory) || !System.IO.Directory.Exists(previewDirectory))
                    throw new ArgumentException("Accordion previews require an existing absolute owned directory.");
                var oldCoordinatorBody = coordinatorDockPanel!.Content; var oldWorkersBody = workersDockPanel!.Content;
                try
                {
                    // Explicit controlled bodies demonstrate the native accordion geometry only, never a live task.
                    var coordinatorPreview = new StackPanel();
                    coordinatorPreview.Children.Add(Ui.Text("Controlled coordinator fixture · read-only", 13));
                    coordinatorPreview.Children.Add(Ui.Text("Retained task state and conversation details occupy this selected body.", 13));
                    coordinatorDockPanel.Content = coordinatorPreview;
                    var workersPreview = new StackPanel();
                    workersPreview.Children.Add(Ui.Text("Controlled Workers fixture · one compact list", 13));
                    foreach (var name in new[] { "Research worker · Waiting", "Review worker · Completed" })
                    {
                        var row = Ui.Button(name, () => throw new InvalidOperationException("Preview rows are never invoked."));
                        CompactWorkPanelControl(row); workersPreview.Children.Add(row);
                    }
                    workersDockPanel.Content = workersPreview;
                    void CapturePeer(Expander panel, string name, int width, int height, double dpi)
                    {
                        OpenConversationPeer(panel);
                        container.Measure(new Size(width, height)); container.Arrange(new Rect(0, 0, width, height)); container.UpdateLayout();
                        Check(peers.Count(item => item.IsExpanded) == 1 && panel.IsExpanded && panel.ActualHeight > 100,
                            "The same-run " + name + " preview retains one actual expanded native body.");
                        var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap((int)(width * dpi / 96), (int)(height * dpi / 96), dpi, dpi, PixelFormats.Pbgra32);
                        var backdrop = new DrawingVisual();
                        using (var drawing = backdrop.RenderOpen()) drawing.DrawRectangle(Ui.Surface, null, new Rect(0, 0, width, height));
                        bitmap.Render(backdrop); bitmap.Render(container);
                        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
                        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
                        using var output = new System.IO.FileStream(System.IO.Path.Combine(previewDirectory, "accordion-" + name + ".png"),
                            System.IO.FileMode.CreateNew, System.IO.FileAccess.Write); encoder.Save(output);
                    }
                    CapturePeer(mainWorkPanel, "main", 1280, 840, 96);
                    CapturePeer(coordinatorDockPanel, "coordinator", 1280, 840, 96);
                    CapturePeer(workersDockPanel, "workers", 1280, 840, 96);
                    CapturePeer(workersDockPanel, "workers-small-150", 720, 540, 144);
                }
                finally
                {
                    coordinatorDockPanel.Content = oldCoordinatorBody; workersDockPanel.Content = oldWorkersBody;
                    OpenConversationPeer(mainWorkPanel);
                    container.Measure(new Size(1280, 840)); container.Arrange(new Rect(0, 0, 1280, 840)); container.UpdateLayout();
                }
            }
            Check(sharedActivityPanel is not null && pages.ContainsKey("activity") &&
                !workPanelLayout!.Children.Contains(sharedActivityPanel) && ReferenceEquals(sharedActivityPanel.Content, sharedActivity),
                "The single existing Activity timeline owns a separate page and is absent from the chat/dock hierarchy.");
            var oldMainExpanded = mainWorkPanel!.IsExpanded;
            SelectPage("activity");
            container.Measure(new Size(720, 540)); container.Arrange(new Rect(0, 0, 720, 540)); container.UpdateLayout();
            Check(ReferenceEquals(Tabs.SelectedItem, pages["activity"]) && mainWorkPanel.IsExpanded == oldMainExpanded &&
                prompt.Text == dockDraft && CurrentConversationId == oldDockOwner &&
                !Children<Expander>(coordinatorDockPanel!).Contains(sharedActivityPanel) &&
                !Children<Expander>(workersDockPanel!).Contains(sharedActivityPanel),
                "Activity navigation preserves Main/draft/identity and cannot create a nested Activity dock row.");
            SelectPage("chat");
            prompt.Text = "Keep this unsent direction.";
            ToggleHeader(workersDockPanel!);
            container.Measure(new Size(1280, 840)); container.Arrange(new Rect(0, 0, 1280, 840)); container.UpdateLayout();
            Check(mainWorkPanel.ActualHeight < 150 && prompt.Text == "Keep this unsent direction.",
                "Selecting Workers releases Main space and preserves its unsent draft.");
            ToggleHeader(mainWorkPanel);
            container.Measure(new Size(1280, 840)); container.Arrange(new Rect(0, 0, 1280, 840)); container.UpdateLayout();
            Check(mainWorkPanel.ActualHeight > 300 && prompt.ActualHeight >= prompt.MinHeight,
                "Reopening Main restores a usable conversation and composer.");
            using var accordion = new WindowPanelAccordion();
            var first = new Expander { Header = "Main", IsExpanded = true };
            var second = new Expander { Header = "Coordinator" };
            var editor = new TextBox { Text = "Retain this plugin draft." };
            var third = new Expander { Header = "Plugin", Content = editor };
            accordion.Register(first); accordion.Register(second); accordion.Register(third);
            second.IsExpanded = true;
            Check(second.IsExpanded && !first.IsExpanded && !third.IsExpanded,
                "Opening another folder collapses the previous one.");
            third.RaiseEvent(new KeyboardFocusChangedEventArgs(Keyboard.PrimaryDevice, 0, second, third)
                { RoutedEvent = Keyboard.GotKeyboardFocusEvent });
            Check(third.IsExpanded && !first.IsExpanded && !second.IsExpanded && editor.Text == "Retain this plugin draft.",
                "The real focus handler opens its panel exclusively without editing its content.");
            first.RaiseEvent(new KeyboardFocusChangedEventArgs(Keyboard.PrimaryDevice, 0, third, first)
                { RoutedEvent = Keyboard.GotKeyboardFocusEvent });
            Check(first.IsExpanded && !second.IsExpanded && !third.IsExpanded,
                "Focusing Main closes worker and plugin folders.");
            return checks;
        }
        finally
        {
            navigation = oldNavigation; navigationSessions = oldSessions; selectedNavigationId = oldSelection;
            sidebarRecentCount = oldDisplayCount; selectedNavigationOccurrence = oldOccurrence;
            navigationFilter.Text = oldFilter; prompt.Text = oldDraft;
            if (mainWorkPanel is not null) mainWorkPanel.IsExpanded = true;
            if (coordinatorDockPanel is not null) coordinatorDockPanel.IsExpanded = false;
            if (workersDockPanel is not null) workersDockPanel.IsExpanded = false;
            activityRequested = oldPanelsRequested; SelectPage("chat"); UpdateConversationLayout();
            RenderNavigation();
        }
    }
}
