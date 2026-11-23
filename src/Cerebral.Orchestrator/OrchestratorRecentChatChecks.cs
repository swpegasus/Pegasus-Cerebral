using System.Windows.Automation;
using System.Windows.Controls;
using Cerebral.Core;

namespace Cerebral.Orchestrator;

public sealed partial class OrchestratorWindow
{
    // Real grouped rendering with controlled summaries; no service, archive/delete or preference writes.
    private int CheckRecentChatNavigationPolicy()
    {
        var oldNavigation = navigation; var oldSessions = navigationSessions; var oldSession = session;
        var oldSelection = selectedNavigationId; var oldOccurrence = selectedNavigationOccurrence;
        var oldLimit = recentChatLimit; var oldAll = showingAllLoadedChats; var oldOlder = readingOlderChats;
        var oldDisplayCount = sidebarRecentCount; var oldCollapsedSections = collapsedNavigationSections.ToArray();
        var oldLegacy = showingLegacyNavigation; var oldFilter = navigationFilter.Text; var oldDraft = prompt.Text;
        var oldDurablePage = durableConversationPage; var oldExactPage = exactWorkflowConversationPage;
        var oldSelectionVersion = conversationSelectionVersion; var oldVisit = conversationHistory.Current;
        var checks = 0;
        void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); checks++; }
        IEnumerable<TreeViewItem> TreeRows(ItemsControl parent, bool includeRecent = false)
        {
            foreach (var row in parent.Items.OfType<TreeViewItem>())
            {
                if (!includeRecent && AutomationProperties.GetAutomationId(row) == "NavigationSection.Recent") continue;
                yield return row;
                foreach (var child in TreeRows(row, includeRecent)) yield return child;
            }
        }
        List<NavigationNode> Rows() => TreeRows(navigationTree).Select(item => item.Tag).OfType<NavigationNode>()
            .Where(node => node.Kind == "conversation").ToList();
        TreeViewItem Group(string key) => navigationTree.Items.OfType<TreeViewItem>().Single(row =>
            AutomationProperties.GetAutomationId(row) == "NavigationSection." + key);
        NavigationNode[] Recent() => Group("Recent").Items.OfType<TreeViewItem>().Select(row => (NavigationNode)row.Tag).ToArray();
        try
        {
            var now = DateTimeOffset.UtcNow;
            navigationSessions = Enumerable.Range(0, 30).Select(index => new TaskSession
            {
                Id = "recent-limit-" + index, ProfileId = "lead", Title = "General " + index, UpdatedAt = now.AddMinutes(index)
            }).ToList();
            navigationSessions.Add(new() { Id = "protected-project", ProfileId = "lead", Title = "Ancient project chat", ProjectId = "protected-root", UpdatedAt = now.AddYears(-1) });
            navigation = new() { Nodes = [
                new() { Id = "protected-root", Kind = "project", Title = "Protected project" },
                new() { Id = "pinned-general", Kind = "conversation", TargetId = "recent-limit-0", Title = "General 0", Pinned = true }
            ] };
            session = navigationSessions[1]; selectedNavigationId = "conversation-recent-limit-1"; selectedNavigationOccurrence = null;
            recentChatLimit = RecentChatPolicy.DefaultLimit; sidebarRecentCount = DefaultSidebarRecentCount;
            collapsedNavigationSections.Clear(); collapsedNavigationSections.Add("Unassigned");
            showingLegacyNavigation = readingOlderChats = showingAllLoadedChats = false;
            durableConversationPage = null; exactWorkflowConversationPage = null;
            navigationFilter.Clear(); RenderNavigation();
            var chats = Rows();
            var expectedIds = navigationSessions.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
            Check(chats.Count == 31 && chats.Select(node => node.TargetId!).ToHashSet(StringComparer.Ordinal).SetEquals(expectedIds),
                "All 31 loaded chat IDs occur exactly once in the canonical folder tree.");
            Check(DefaultSidebarRecentCount == 5 && Recent().Length == 5 &&
                Recent().Select(node => node.TargetId).SequenceEqual(Enumerable.Range(25, 5).Reverse().Select(index => "recent-limit-" + index)) &&
                ReferenceEquals(navigationTree.Items[0], Group("Recent")) &&
                AutomationProperties.GetAutomationId((TreeViewItem)navigationTree.Items[1]) == "NavigationSection.Projects",
                "Recent shows the five newest same-ID shortcuts before Projects and the full folder tree.");
            Check(!Group("Unassigned").IsExpanded && Group("Unassigned").Tag is null && Group("Unassigned").ContextMenu is null &&
                Group("Unassigned").Items.Count == 30 && navigation.Nodes.Count == 2,
                "Unassigned starts collapsed and has no stored node, project assignment or destructive context menu.");
            Check(chats.Any(node => node.TargetId == session.Id) && chats.Count(node => node.TargetId == "recent-limit-0") == 1,
                "The selected older chat and old pin remain discoverable once in the canonical tree.");
            Check(chats.Any(node => node.TargetId == "recent-limit-2") && chats.Any(node => node.TargetId == "recent-limit-29"),
                "Older loaded chats remain reachable alongside the newest chat.");
            Check(chats.Any(node => node.TargetId == "protected-project") && AutomationProperties.GetName(navigationTree) == "All chats" &&
                TreeRows(navigationTree).Single(row => row.Tag is NavigationNode { Id: "protected-root" }).Items.OfType<TreeViewItem>()
                    .Any(row => row.Tag is NavigationNode { TargetId: "protected-project" }),
                "Ancient project chats remain reachable under their actual project independently of Recent.");
            Check(olderChats?.IsEnabled == false && !HasHiddenRecentChats(EffectiveNavigation()),
                "Grouping loaded rows does not require a needless owner history read.");
            navigationFilter.Text = "General 2";
            Check(Rows().Any(node => node.TargetId == "recent-limit-2"),
                "Sidebar search must find an older loaded ordinary chat without applying the recent cap.");
            navigationFilter.Text = "Protected project";
            Check(Rows().Any(node => node.TargetId == "protected-project") && Rows().Any(node => node.TargetId == session.Id) &&
                !Rows().Any(node => node.TargetId == "recent-limit-0"),
                "Project-name search must find its chats, retain the selected chat and filter unrelated chats.");
            navigationFilter.Clear();
            showingAllLoadedChats = true; RenderNavigation();
            Check(Rows().Count == 31,
                "The legacy loaded-history flag cannot hide or duplicate the already visible chats.");
            showingAllLoadedChats = false; RenderNavigation();
            Check(Rows().Count == 31 && (navigationTree.SelectedItem as TreeViewItem)?.Tag is NavigationNode { TargetId: "recent-limit-1" },
                "Returning to current-chat mode must preserve all loaded rows and the selected older chat.");
            recentChatLimit = 15; RenderNavigation();
            Check(Rows().Count == 31 && Recent().Length == 5,
                "A saved 15-chat server preference changes neither full loaded coverage nor the separate five-chat display preference.");
            var heldNavigation = navigation; var heldSessions = navigationSessions;
            SetSidebarRecentCount(10);
            Check(Recent().Length == 10 && Rows().Count == 31 && recentChatLimit == 15 &&
                ReferenceEquals(navigation, heldNavigation) && ReferenceEquals(navigationSessions, heldSessions) &&
                durableConversationPage is null && exactWorkflowConversationPage is null && !IsLoaded && !IsVisible,
                "Changing the unshown fixture's Recent display count preserves source data, server preference and page cursors without a preference write.");
            var badCountRefused = false;
            try { SetSidebarRecentCount(201); } catch (ArgumentOutOfRangeException) { badCountRefused = true; }
            Check(badCountRefused && sidebarRecentCount == 10, "Recent display rejects out-of-range values without changing its accepted count.");
            SetSidebarRecentCount(5);
            Group("Unassigned").IsExpanded = true; RenderNavigation();
            Check(Group("Unassigned").IsExpanded, "Human expansion of Unassigned survives a render.");
            Group("Unassigned").IsExpanded = false; RenderNavigation();
            Check(!Group("Unassigned").IsExpanded, "Human collapse of Unassigned survives a render.");
            navigation.Nodes.Add(new() { Id = "plain-folder", Kind = "folder", Title = "General folder" });
            navigation.Nodes.Add(new() { Id = "folder-old", Kind = "conversation", TargetId = "recent-limit-2", Title = "General 2", ParentId = "plain-folder" });
            navigation.Nodes.Add(new() { Id = "folder-new", Kind = "conversation", TargetId = "recent-limit-29", Title = "General 29", ParentId = "plain-folder" });
            navigation.Nodes.Add(new() { Id = "grouped-project-old", Kind = "conversation", TargetId = "recent-limit-3", Title = "General 3", ParentId = "protected-root" });
            RenderNavigation();
            Check(Rows().Count == 31 && Rows().Select(node => node.TargetId!).ToHashSet(StringComparer.Ordinal).SetEquals(expectedIds) &&
                TreeRows(navigationTree).Single(row => row.Tag is NavigationNode { Id: "plain-folder" }).Items.OfType<TreeViewItem>()
                    .Any(row => row.Tag is NavigationNode { TargetId: "recent-limit-2", ParentId: "plain-folder" }) &&
                !Group("Unassigned").Items.OfType<TreeViewItem>().Any(row => row.Tag is NavigationNode
                    { TargetId: "recent-limit-2" or "recent-limit-29" or "recent-limit-3" }),
                "Explicit folder/project membership stays in the real tree without duplicate canonical IDs or incorrect Unassigned aliases.");
            Check(Rows().Any(node => node.TargetId == "recent-limit-3" && node.ParentId == "protected-root"),
                "Stored project ancestry remains attached to the same canonical chat.");
            durableConversationPage = new([], 40, 20); RenderNavigation();
            Check(olderChats?.IsEnabled == true && Rows().Count == 31,
                "A real durable continuation enables Older chats without hiding loaded rows or fetching automatically.");
            Check(navigationSessions.Count == 31 && navigation.Nodes.Count == 6 && prompt.Text == oldDraft &&
                conversationSelectionVersion == oldSelectionVersion && conversationHistory.Current == oldVisit && CurrentConversationId == session.Id,
                "Rendering and search must preserve sessions, organization, draft, owner selection and visit history.");
            navigation.Nodes.AddRange([
                new() { Id = "duplicate-folder-a", Kind = "folder", Title = "First duplicate folder" },
                new() { Id = "duplicate-folder-b", Kind = "folder", Title = "Second duplicate folder" },
                new() { Id = "duplicate-a", Kind = "conversation", TargetId = "recent-limit-4", Title = "Canonical duplicate title",
                    ParentId = "duplicate-folder-a", Order = 0 },
                new() { Id = "duplicate-b", Kind = "conversation", TargetId = "recent-limit-4", Title = "Nonwinning alias search phrase",
                    ParentId = "duplicate-folder-b", Order = 1 }
            ]);
            var duplicateMetadata = CerebralJson.Element(navigation).GetRawText();
            var duplicatePage = durableConversationPage; var duplicateExactPage = exactWorkflowConversationPage;
            var duplicateSelection = selectedNavigationId; var duplicateOccurrence = selectedNavigationOccurrence;
            RenderNavigation();
            Check(Rows().Count == 31 && Rows().Select(node => node.TargetId!).ToHashSet(StringComparer.Ordinal).SetEquals(expectedIds) &&
                Rows().Single(node => node.TargetId == "recent-limit-4") is { Id: "duplicate-a", ParentId: "duplicate-folder-a" },
                "Distinct admitted node IDs for one chat yield exactly 31 canonical occurrences and the full 31-ID set, preserving a stable real folder placement.");
            navigationFilter.Text = "Nonwinning alias search phrase";
            Check(Rows().Count(node => node.TargetId == "recent-limit-4") == 1 &&
                Rows().Single(node => node.TargetId == "recent-limit-4").Id == "duplicate-a" &&
                selectedNavigationId == duplicateSelection && selectedNavigationOccurrence == duplicateOccurrence,
                "Searching a nonwinning stored alias reaches the same canonical chat without changing its placement or selection.");
            navigationFilter.Clear();
            selectedNavigationId = "duplicate-b"; selectedNavigationOccurrence = "Navigation-duplicate-b"; RenderNavigation();
            Check(Rows().Count == 31 && Rows().Select(node => node.TargetId!).ToHashSet(StringComparer.Ordinal).SetEquals(expectedIds) &&
                Rows().Single(node => node.TargetId == "recent-limit-4") is { Id: "duplicate-b", ParentId: "duplicate-folder-b" } &&
                navigationTree.SelectedItem is TreeViewItem { IsSelected: true, Tag: NavigationNode { Id: "duplicate-b" } },
                "An explicitly selected stored occurrence stays the single canonical row under its actual folder.");
            selectedNavigationId = duplicateSelection; selectedNavigationOccurrence = duplicateOccurrence; RenderNavigation();
            Check(CerebralJson.Element(navigation).GetRawText() == duplicateMetadata &&
                navigationSessions.Count == 31 && recentChatLimit == 15 && sidebarRecentCount == 5 &&
                ReferenceEquals(durableConversationPage, duplicatePage) && ReferenceEquals(exactWorkflowConversationPage, duplicateExactPage) &&
                conversationSelectionVersion == oldSelectionVersion && conversationHistory.Current == oldVisit &&
                prompt.Text == oldDraft && !IsConnected && !IsLoaded && !IsVisible,
                "Canonical filtering and alias search preserve exact stored metadata, IDs, server preference/cursors, draft and history without an owner call or preference write.");
            Check(RecentChatPolicy.Effective(null) == 20 && RecentChatPolicy.IsValid(1) && RecentChatPolicy.IsValid(200) &&
                !RecentChatPolicy.IsValid(0) && !RecentChatPolicy.IsValid(201), "Existing server preference metadata keeps its bounds independently of the sidebar display setting.");
            return checks;
        }
        finally
        {
            navigation = oldNavigation; navigationSessions = oldSessions; session = oldSession;
            selectedNavigationId = oldSelection; selectedNavigationOccurrence = oldOccurrence;
            recentChatLimit = oldLimit; sidebarRecentCount = oldDisplayCount;
            collapsedNavigationSections.Clear(); foreach (var key in oldCollapsedSections) collapsedNavigationSections.Add(key);
            showingAllLoadedChats = oldAll; readingOlderChats = oldOlder;
            showingLegacyNavigation = oldLegacy; durableConversationPage = oldDurablePage; exactWorkflowConversationPage = oldExactPage;
            navigationFilter.Text = oldFilter; RenderNavigation();
        }
    }
}
