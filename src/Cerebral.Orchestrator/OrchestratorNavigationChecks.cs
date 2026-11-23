using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Cerebral.Core;

namespace Cerebral.Orchestrator;

public sealed partial class OrchestratorWindow
{

    // Runs only in a dedicated unconnected fixture window. Reads are held deterministic delegates;
    // the real refresh, grouped-row selection and New handlers execute without a Worker or shown window.
    private async Task<int> CheckPassiveNavigationRefreshAsync(CancellationToken cancellationToken)
    {
        var checks = 0;
        void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
            checks++;
        }
        NavigationState State(int revision, bool includeProject = true) => new()
        {
            Revision = revision,
            Nodes = includeProject
                ? [new() { Id = "refresh-project", Kind = "project", Title = "Refresh project", Path = @"C:\Projects\Refresh" },
                   new() { Id = "refresh-folder", Kind = "folder", Title = "Nested folder", ParentId = "refresh-project" }]
                : []
        };
        TreeViewItem Row(string id)
        {
            TreeViewItem? Find(ItemsControl parent)
            {
                foreach (var item in parent.Items.OfType<TreeViewItem>())
                {
                    if (item.Tag is NavigationNode node && node.Id == id) return item;
                    if (Find(item) is { } found) return found;
                }
                return null;
            }
            return Find(navigationTree) ?? throw new InvalidOperationException("Missing fixture row: " + id);
        }
        TaskCompletionSource<NavigationState> Hold() => new(TaskCreationOptions.RunContinuationsAsynchronously);
        void General(int revision, bool includeProject = true)
        {
            navigation = State(revision, includeProject); navigationSessions = [];
            profiles = [new() { Id = "refresh-lead", Name = "Cerebral", Role = "lead",
                WorkerId = "local", AllowedRoots = [@"C:\Projects"] }];
            leadProfile = profiles[0]; projectsDirectory = @"C:\Projects";
            session = null; durableConversationId = Guid.NewGuid().ToString("N"); draftProjectId = null;
            selectedNavigationId = NavigationIdForConversation(durableConversationId);
            selectedNavigationOccurrence = null; navigationFilter.Clear(); RenderNavigation();
        }

        cancellationToken.ThrowIfCancellationRequested();
        General(1, false);
        var originalId = CurrentConversationId!;
        prompt.Text = "Retain this unsent composer";
        var held = Hold(); var reads = 0;
        var pending = RefreshNavigationAsync([], () => { reads++; return held.Task.WaitAsync(cancellationToken); });
        Check(!pending.IsCompleted, "Refresh must actually suspend before the simulated Add acknowledgement.");
        // Project registration has succeeded while its navigation.updated refresh is still in flight.
        navigation = State(2); selectedNavigationId = "refresh-project"; RenderNavigation();
        held.SetResult(State(2)); await pending;
        Check(reads == 1 && navigation.Revision == 2 && selectedNavigationId == "refresh-project",
            "Add acknowledgement selection must survive its delayed equal-revision refresh.");
        Check(Row("refresh-project").Tag is NavigationNode { Kind: "project" } &&
            Row("refresh-folder").Tag is NavigationNode { ParentId: "refresh-project", Kind: "folder" } &&
            Row("refresh-folder").Parent == Row("refresh-project"),
            "Project registration retains its actual project/folder ancestry beneath Recent.");
        Check(CurrentConversationId == originalId && draftProjectId is null && prompt.Text == "Retain this unsent composer",
            "Passive refresh must not rebind or clear the current general draft.");
        NewConversationInProject("refresh-project");
        Check(CurrentConversationId != originalId && draftProjectId == "refresh-project",
            "The project-menu New action after Add must create a fresh project-bound draft.");
        Check(drafts.GetValueOrDefault(originalId) == "Retain this unsent composer",
            "Ordinary New must preserve the prior composer under its original identity.");
        var projectDraft = CurrentConversationId!;
        await RefreshNavigationAsync([], () => Task.FromResult(State(2)));
        Check(selectedNavigationId == NavigationIdForConversation(projectDraft) && Row(selectedNavigationId).IsSelected &&
            draftProjectId == "refresh-project", "A new project's synthetic chat row must remain selected on later refresh.");

        General(2);
        draftProjectId = "refresh-project"; RenderNavigation();
        held = Hold(); pending = RefreshNavigationAsync([], () => held.Task.WaitAsync(cancellationToken));
        var selectedChatId = NavigationIdForConversation(CurrentConversationId!);
        selecting = true;
        try { Row(selectedChatId).IsSelected = false; }
        finally { selecting = false; }
        selectedNavigationId = null;
        Row(selectedChatId).IsSelected = true;
        Check(selectedNavigationId == selectedChatId,
            "The real row handler must observe the current project-bound chat without an owner read.");
        held.SetResult(State(2)); await pending;
        Check(selectedNavigationId == selectedChatId && Row(selectedChatId).IsSelected && ProjectForNewConversation() == "refresh-project",
            "A chat selection made after refresh starts must retain its row and project binding.");
        NewConversation();
        Check(draftProjectId == "refresh-project", "Ordinary New from the preserved chat selection must bind its project.");

        General(2);
        navigationSessions = [new() { Id = "refresh-saved", ProfileId = "refresh-lead", Title = "Saved chat", ProjectId = "refresh-project" }];
        RenderNavigation();
        held = Hold(); pending = RefreshNavigationAsync(navigationSessions, () => held.Task.WaitAsync(cancellationToken));
        navigationFilter.Text = "Refresh project";
        held.SetResult(State(3)); await pending;
        Check(navigation.Revision == 3 && Row("conversation-refresh-saved").Tag is NavigationNode { ParentId: "refresh-project" } &&
            Row(NavigationIdForConversation(CurrentConversationId!)).IsSelected,
            "Project-name search and the current draft selection survive newer metadata with the actual project path.");
        Check(draftProjectId is null, "Searching a project must not rebind the existing general draft.");

        General(1, false);
        held = Hold(); pending = RefreshNavigationAsync([], () => held.Task.WaitAsync(cancellationToken));
        navigation = State(2); selectedNavigationId = NavigationIdForConversation(CurrentConversationId!);
        var acceptedNavigation = navigation; var acceptedSessions = new List<TaskSession>();
        navigationSessions = acceptedSessions; RenderNavigation();
        held.SetResult(State(1, false)); await pending;
        Check(ReferenceEquals(navigation, acceptedNavigation) && ReferenceEquals(navigationSessions, acceptedSessions),
            "A delayed older revision must not replace either accepted metadata or its paired session projection.");
        Check(navigation.Revision == 2 && navigation.Nodes.Any(node => node.Id == "refresh-project") &&
            Row(NavigationIdForConversation(CurrentConversationId!)).IsSelected,
            "A stale pre-Add read must not remove saved metadata or steal the selected chat row.");

        General(2);
        selectedNavigationId = "refresh-project"; RenderNavigation();
        await RefreshNavigationAsync([], () => Task.FromResult(State(3, false)));
        Check(selectedNavigationId == NavigationIdForConversation(CurrentConversationId!) &&
            Row(selectedNavigationId).IsSelected && ProjectForNewConversation() is null,
            "A removed selected project must fall back to the active general conversation, without stale project inheritance.");
        selectedNavigationId = null;
        await RefreshNavigationAsync([], () => Task.FromResult(State(4)));
        Check(selectedNavigationId == NavigationIdForConversation(CurrentConversationId!),
            "An absent selection must fall back to the active conversation.");
        durableConversationId = null; session = null; selectedNavigationId = "removed-node";
        await RefreshNavigationAsync([], () => Task.FromResult(State(5)));
        Check(selectedNavigationId is null, "Without a current conversation, an invalid selection must clear safely.");

        General(6);
        var groupedDraft = CurrentConversationId; var groupedText = prompt.Text;
        TreeViewItem Unassigned() => navigationTree.Items.OfType<TreeViewItem>().Single(item =>
            AutomationProperties.GetAutomationId(item) == "NavigationSection.Unassigned");
        held = Hold(); pending = RefreshNavigationAsync([], () => held.Task.WaitAsync(cancellationToken));
        Unassigned().IsExpanded = true; Unassigned().IsSelected = true;
        held.SetResult(State(7)); await pending;
        Check(Unassigned().IsExpanded && Unassigned().IsSelected && selectedNavigationId is null &&
            CurrentConversationId == groupedDraft && prompt.Text == groupedText && Unassigned().Tag is null && Unassigned().ContextMenu is null,
            "A delayed metadata refresh preserves human virtual-folder expansion/selection without rebinding the draft or enabling project actions.");
        held = Hold(); pending = RefreshNavigationAsync([], () => held.Task.WaitAsync(cancellationToken));
        Unassigned().IsExpanded = false;
        held.SetResult(State(8)); await pending;
        Check(!Unassigned().IsExpanded && Unassigned().IsSelected && CurrentConversationId == groupedDraft,
            "A delayed metadata refresh never reopens the human-collapsed Unassigned group.");

        General(9);
        held = Hold(); pending = RefreshNavigationAsync([], () => held.Task.WaitAsync(cancellationToken));
        knowledgeConnectionVersion++; navigation = State(0, false); selectedNavigationId = null;
        held.SetResult(State(99)); await pending;
        Check(navigation.Revision == 0 && selectedNavigationId is null,
            "A read from the previous source generation must not apply even with a higher revision.");
        await RefreshNavigationAsync([], () => Task.FromResult(State(1)));
        Check(navigation.Revision == 1, "The current source may accept its own lower revision after a connection reset.");

        General(2);
        held = Hold(); pending = RefreshNavigationAsync([], () => held.Task.WaitAsync(cancellationToken));
        NewConversationInProject("refresh-project");
        var newerConversation = CurrentConversationId;
        var newerRow = selectedNavigationId;
        held.SetResult(State(99, false)); await pending;
        Check(CurrentConversationId == newerConversation && selectedNavigationId == newerRow &&
            draftProjectId == "refresh-project" && navigation.Revision == 2,
            "An earlier refresh must not cross a real New-conversation selection transition.");
        return checks;
    }

    // Exercises the actual WPF tree with deterministic data; never connects, invokes tools or creates projects.
    public int CheckNavigationUi(string? previewPath = null)
    {
        TabItem? fixtureSidebarTab = null;
        var checks = 0;
        void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
            checks++;
        }
        TreeViewItem Find(string id)
        {
            TreeViewItem? Walk(ItemsControl parent)
            {
                foreach (var item in parent.Items.OfType<TreeViewItem>())
                {
                    if (AutomationProperties.GetAutomationId(item) == id) return item;
                    if (Walk(item) is { } child) return child;
                }
                return null;
            }
            return Walk(navigationTree) ?? throw new InvalidOperationException("Missing tree item: " + id);
        }
        bool Exists(string id)
        {
            try { Find(id); return true; } catch (InvalidOperationException) { return false; }
        }
        static Rect Bounds(FrameworkElement element, FrameworkElement ancestor)
        {
            var origin = element.TransformToAncestor(ancestor).Transform(new Point());
            return new Rect(origin, new Size(element.ActualWidth, element.ActualHeight));
        }
        static double Rounded(double value) => Math.Round(value, 2);
        void ResetRails() { workspaceRequested = true; activityRequested = false; compactWorkspaceOpen = false; }
        void Rejected(string parent, string folder)
        {
            try { NewProjectPath(parent, folder); throw new InvalidOperationException("An invalid project path was accepted."); }
            catch (ArgumentException) { checks++; }
        }
        IEnumerable<TreeViewItem> CanonicalTree(ItemsControl parent)
        {
            foreach (var item in parent.Items.OfType<TreeViewItem>())
            {
                if (AutomationProperties.GetAutomationId(item) == "NavigationSection.Recent") continue;
                yield return item;
                foreach (var child in CanonicalTree(item)) yield return child;
            }
        }
        NavigationNode[] CanonicalChats() => CanonicalTree(navigationTree).Select(item => item.Tag)
            .OfType<NavigationNode>().Where(node => node.Kind == "conversation").ToArray();
        try
        {
            sidebarRecentCount = DefaultSidebarRecentCount; wrapChatNames = false;
            collapsedNavigationSections.Clear(); collapsedNavigationSections.Add("Unassigned");
            var now = DateTimeOffset.UtcNow;
            projectsDirectory = @"C:\Projects";
            profiles =
            [
                new() { Id = "lead", Name = "Cerebral", Role = "lead", WorkerId = "local", AllowedRoots = [@"C:\Projects"] },
                new() { Id = "helper", Name = "Coding", Role = "helper" }
            ];
            leadProfile = profiles[0];
            navigationSessions =
            [
                new() { Id = "old", ProfileId = "lead", Title = "Chapter outline", ProjectId = "book", UpdatedAt = now.AddDays(-1) },
                new() { Id = "recent", ProfileId = "lead", Title = "Prepare locations", ProjectId = "book", UpdatedAt = now },
                new() { Id = "general", ProfileId = "lead", Title = "General discussion" }
            ];
            navigation = new NavigationState
            {
                Nodes =
                [
                    new() { Id = "writing", Kind = "folder", Title = "Writing" },
                    new() { Id = "series", Kind = "folder", Title = "Novel series", ParentId = "writing" },
                    new() { Id = "book", Kind = "project", Title = "Northwind", ParentId = "series", Path = @"C:\Projects\Northwind", Pinned = true },
                    new() { Id = "stale-agent", Kind = "agent", Title = "Removed agent", TargetId = "removed", Pinned = true }
                ]
            };
            activeJobs = [new() { Id = "job", SessionId = "recent", ProfileId = "lead", Status = "waiting-human" }];
            selectedNavigationId = "conversation-recent"; selectedNavigationOccurrence = null;
            session = navigationSessions[1];
            RenderNavigation();
            var recentGroup = Find("NavigationSection.Recent");
            var projectsGroup = Find("NavigationSection.Projects");
            var unassignedGroup = Find("NavigationSection.Unassigned");
            var recentRows = recentGroup.Items.OfType<TreeViewItem>().ToArray();
            Check(ReferenceEquals(navigationTree.Items[0], recentGroup) && recentGroup.IsExpanded &&
                navigationTree.Items.Count == 3 && ReferenceEquals(navigationTree.Items[1], projectsGroup) &&
                ReferenceEquals(navigationTree.Items[2], unassignedGroup) && projectsGroup.IsExpanded && recentRows.Length == 3 &&
                recentRows.All(item => item.Tag is NavigationNode { Kind: "conversation" } && item.Items.Count == 0),
                "Recent, Projects and Unassigned are the only three fixed headers, with the real tree under Projects.");
            var canonicalChats = CanonicalChats();
            Check(canonicalChats.Length == 3 && canonicalChats.Select(node => node.TargetId).Distinct().Count() == 3 &&
                canonicalChats.Select(node => node.TargetId).ToHashSet().SetEquals(new[] { "old", "recent", "general" }) &&
                Find("Navigation-writing").Parent == projectsGroup && Find("Navigation-series").Parent == Find("Navigation-writing") &&
                Find("Navigation-book").Parent == Find("Navigation-series"),
                "All three canonical chat IDs and their real nested folder/project ancestry remain available.");
            Check(Array.FindIndex(recentRows, item => item.Tag is NavigationNode { TargetId: "recent" }) <
                Array.FindIndex(recentRows, item => item.Tag is NavigationNode { TargetId: "old" }) &&
                recentRows.All(item => canonicalChats.Any(node => ReferenceEquals(node, item.Tag))),
                "Recent uses saved activity recency and aliases the same canonical nodes rather than copying chat state.");
            Check(!unassignedGroup.IsExpanded && unassignedGroup.Tag is null && unassignedGroup.ContextMenu is null &&
                unassignedGroup.Items.OfType<TreeViewItem>().Single().Tag is NavigationNode { TargetId: "general", ParentId: null } &&
                !navigation.Nodes.Any(node => node.Id == "Unassigned"),
                "The folder-style Unassigned group starts collapsed without creating a real folder or changing project bindings.");
            var projectActions = projectsGroup.ContextMenu?.Items.OfType<MenuItem>().ToArray() ?? [];
            var projectButton = NavigationDescendant<Button>(projectsGroup.Header as DependencyObject ?? projectsGroup);
            Check(recentGroup.Tag is null && recentGroup.ContextMenu is null && projectsGroup.Tag is null &&
                projectActions.Select(AutomationProperties.GetAutomationId).SequenceEqual(new[]
                { "NavigationProjects.NewFolder", "NavigationProjects.NewProject", "NavigationProjects.AddExistingProject" }) &&
                projectButton is not null && AutomationProperties.GetAutomationId(projectButton) == "NavigationProjectsActions" &&
                AutomationProperties.GetName(projectButton) == "Project and folder actions" &&
                !navigation.Nodes.Any(node => node.Id is "Recent" or "Projects" or "Unassigned"),
                "Fixed headers have no rename/remove identity, and Projects exposes three accessible root-create actions.");
            var removalFolder = new NavigationNode { Id = "remove-empty", Kind = "folder", Title = "Empty folder", ParentId = "keep-folder" };
            var removalState = new NavigationState { Revision = 41, Nodes = [
                new() { Id = "keep-folder", Kind = "folder", Title = "Keep folder" }, removalFolder,
                new() { Id = "keep-project", Kind = "project", Title = "Keep project", Path = @"C:\Projects\Keep" },
                new() { Id = "keep-chat", Kind = "conversation", Title = "Unloaded alias", TargetId = "unloaded-chat", ParentId = "keep-project" }] };
            var removalBefore = CerebralJson.Element(removalState);
            var removed = PrepareEmptyNavigationFolderRemoval(removalState, removalFolder, 41);
            Check(removed.Revision == 41 && removed.Nodes.Count == 3 && removed.Nodes.All(node => node.Id != removalFolder.Id) &&
                removed.Nodes.Select(node => node.Id).SequenceEqual(new[] { "keep-folder", "keep-project", "keep-chat" }) &&
                System.Text.Json.JsonElement.DeepEquals(CerebralJson.Element(removalState), removalBefore) &&
                System.Text.Json.JsonElement.DeepEquals(CerebralJson.Element(removed.Nodes[2]), CerebralJson.Element(removalState.Nodes[3])),
                "Empty-folder removal prepares one metadata-only CAS edit without changing input state, project or unloaded chat alias.");
            bool RemovalRejected(NavigationState state, NavigationNode expected, int revision)
            {
                try { _ = PrepareEmptyNavigationFolderRemoval(state, expected, revision); return false; }
                catch (InvalidOperationException) { return true; }
            }
            var occupied = CerebralJson.Read<NavigationState>(removalBefore);
            occupied.Nodes.Add(new() { Id = "hidden-child", Kind = "agent", Title = "Hidden child", ParentId = removalFolder.Id });
            var changedFolder = CerebralJson.Read<NavigationNode>(CerebralJson.Element(removalFolder)); changedFolder.Title = "Changed";
            Check(RemovalRejected(removalState, removalFolder, 40) && RemovalRejected(occupied, removalFolder, 41) &&
                RemovalRejected(removalState, changedFolder, 41) &&
                RemovalRejected(removalState, removalState.Nodes[2], 41) &&
                RemovalRejected(removalState, new() { Id = "NavigationSection.Projects", Kind = "folder", Title = "Projects" }, 41),
                "Removal refuses stale revision, hidden children, changed exact folder, project nodes and virtual fixed headers.");
            var mainNavigation = navigation;
            try
            {
                navigation = removalState;
                Check(NavigationMenu(removalFolder).Items.OfType<MenuItem>().Single(item =>
                    AutomationProperties.GetAutomationId(item) == "Navigation.RemoveEmptyFolder").IsEnabled &&
                    !NavigationMenu(removalState.Nodes[2]).Items.OfType<MenuItem>().Any(item =>
                        AutomationProperties.GetAutomationId(item) == "Navigation.RemoveEmptyFolder"),
                    "Only an empty plain folder offers enabled Remove; projects do not offer this action.");
                navigation = occupied;
                Check(!NavigationMenu(removalFolder).Items.OfType<MenuItem>().Single(item =>
                    AutomationProperties.GetAutomationId(item) == "Navigation.RemoveEmptyFolder").IsEnabled,
                    "A hidden canonical child keeps the folder removal action disabled.");
            }
            finally { navigation = mainNavigation; }
            Check(!Exists("Navigation-stale-agent") && !Exists("Navigation-agent-lead") && !Exists("NavigationSection-Agents") &&
                AutomationProperties.GetName(navigationTree) == "All chats",
                "The accessible chat rail keeps the real folder tree while omitting stale agent rows.");
            var oldSelection = (navigationTree.SelectedItem as TreeViewItem)?.Tag as NavigationNode;
            Check(oldSelection?.Id == "conversation-recent", "The currently open conversation must remain selected.");
            checks += ConversationVisitHistoryChecks.Run();
            var fixtureSession = session;
            var fixtureDurableId = durableConversationId;
            var fixtureProject = draftProjectId;
            session = null; durableConversationId = "fixture-unsent"; draftProjectId = null;
            selectedNavigationId = "conversation-fixture-unsent";
            navigationFilter.Text = "no matching title";
            RenderNavigation();
            Check(Find("Recent-conversation-fixture-unsent").IsSelected,
                "A new general chat must keep its own sidebar row selected under a search filter.");
            draftProjectId = "book";
            selectedNavigationId = NavigationIdForConversation("fixture-unsent");
            RenderNavigation();
            Check(Find("Recent-conversation-fixture-unsent").IsSelected &&
                Find("Recent-conversation-fixture-unsent").Tag is NavigationNode { ParentId: "book", TargetId: "fixture-unsent" } &&
                Exists("Navigation-book"),
                "Changing an unsent chat's project retains the selected Recent row and its real project path under search.");
            var unsentReadback = CerebralJson.Element(DesktopNavigationControlSnapshot());
            Check(unsentReadback.GetProperty("selectedRow").GetProperty("targetId").GetString() == "fixture-unsent" &&
                unsentReadback.GetProperty("selectedRow").GetProperty("projectId").GetString() == "book" &&
                unsentReadback.GetProperty("selectedRow").GetProperty("isSelected").GetBoolean() &&
                unsentReadback.GetProperty("projectForNewConversation").GetString() == "book",
                "Passive readback must observe the actual selected synthetic row and its project binding.");
            Check(ProjectForNewConversation() == "book",
                "New chat from the selected synthetic row must inherit the current project's binding.");
            session = fixtureSession; durableConversationId = fixtureDurableId; draftProjectId = fixtureProject;
            selectedNavigationId = "conversation-recent"; navigationFilter.Clear(); RenderNavigation();
            var savedOrganization = navigation.Nodes.Select(node => (node.Id, node.ParentId, node.Pinned)).ToArray();
            navigationFilter.Text = "Prepare";
            Check(Exists("Navigation-conversation-recent") && CanonicalChats().Length == 1 &&
                Find("Navigation-conversation-recent").Parent == Find("Navigation-book"),
                "Title search returns the matching canonical chat with its actual folder/project path.");
            Check(!Exists("Navigation-conversation-old"), "Search must omit nonmatching sibling conversations.");
            navigationFilter.Text = "Northwind";
            Check(Exists("Navigation-conversation-recent") && Exists("Navigation-conversation-old") &&
                !Exists("Navigation-conversation-general") && CanonicalChats().Length == 2 &&
                Find("Navigation-book").Items.Count == 2,
                "Project-name search returns both project chats while retaining their real parent.");
            navigationFilter.Clear();
            Check(savedOrganization.SequenceEqual(navigation.Nodes.Select(node => (node.Id, node.ParentId, node.Pinned))),
                "Grouped rendering and search preserve stored project, folder and pin organization.");
            RenderNavigation();
            Check(AutomationProperties.GetAutomationId((TreeViewItem)navigationTree.SelectedItem) == "Recent-conversation-recent",
                "Refreshing the grouped list retains the exact selected Recent occurrence.");
            Check(pinNavigationButton?.IsEnabled == true && moreNavigationButton?.IsEnabled == true &&
                BuildChatActionsMenu().Items.OfType<MenuItem>().Any(item => AutomationProperties.GetAutomationId(item) ==
                    AutomationProperties.GetAutomationId(pinNavigationButton!)),
                "Selected chat organization actions must remain available through the application menu.");
            checks += CheckRecentChatNavigationPolicy();
            Check(DefaultProjectRoot(profiles[0]) == @"C:\Projects" && DefaultFileRoot(profiles[0]) == @"C:\Projects\Northwind",
                "New projects use the configured root while project conversations browse their own folder.");
            profiles[0].AllowedRoots = [@"C:\Other"];
            Check(DefaultFileRoot(profiles[0]) == @"C:\Other", "The file browser must not infer access grants from the default project setting.");
            profiles[0].AllowedRoots = [@"C:\Projects"];
            Check(NewProjectPath(@"C:\Projects", "First book") == @"C:\Projects\First book", "A project name must produce one child folder.");
            Check(NewProjectPath("/projects", "First book") == "/projects/First book", "A Linux worker project path must keep its native separators.");
            Check(ResolveNewProjectPath(@"C:\Projects", "HelloWorld", null) == @"C:\Projects\HelloWorld",
                "A quick folder name resolves only under the configured Cerebral projects root.");
            Check(ResolveNewProjectPath(@"C:\Projects", @"D:\Selected\HelloWorld", @"D:\Selected\HelloWorld") == @"D:\Selected\HelloWorld",
                "A complete outside location is accepted only when it is the exact path returned by Browse.");
            try
            {
                _ = ResolveNewProjectPath(@"C:\Projects", @"D:\Typed\HelloWorld", null);
                throw new InvalidOperationException("A manually typed outside project path bypassed Browse.");
            }
            catch (ArgumentException) { checks++; }
            Rejected(@"C:\Projects", "..");
            Rejected(@"C:\Projects", @"outside\book");
            Rejected(@"C:\Projects", "CON.txt");
            Rejected("relative-parent", "book");
            Rejected(@"C:\Projects", "name.");
            Check(!NavigationPathInside(@"C:\Projects-old\Book", @"C:\Projects"),
                "A neighboring directory with a common text prefix must not count as an allowed root.");
            Check(!NavigationPathInside("/projects/../other", "/projects"), "Linux traversal must not pass scope checks.");
            selectedNavigationId = "conversation-recent"; selectedNavigationOccurrence = null;
            RenderNavigation();
            sessionHeading.Text = "Prepare locations";
            sessionState.Text = "Waiting for your answer in this conversation.";
            agentSummary.Text = "Cerebral · 1 helper available · Local coding model";
            projectKnowledgeSummary.Text = "Northwind · Cortex: configured · automatic retrieval";
            var container = (FrameworkElement)Content;
            container.Measure(new Size(1440, 960)); container.Arrange(new Rect(0, 0, 1440, 960)); container.UpdateLayout();
            Check(navigationTree.ActualWidth >= 225 && Find("Recent-conversation-recent").ActualWidth > 0,
                "The actual grouped list lays out with useful width and a visible selected Recent chat.");
            var savedSessions = navigationSessions;
            var savedNavigationId = selectedNavigationId;
            var savedOccurrence = selectedNavigationOccurrence;
            var savedReveal = revealedNavigation;
            var savedPendingReveal = pendingNavigationReveal;
            var savedSuppressedRevealId = suppressedNavigationRevealId;
            try
            {
                navigationSessions = [.. savedSessions];
                for (var index = 0; index < 36; index++)
                    navigationSessions.Add(new TaskSession { Id = "reveal-row-" + index,
                        ProfileId = "lead", Title = "Project chat " + index, ProjectId = "book",
                        UpdatedAt = now.AddDays(-2).AddMinutes(-index) });
                var revealSession = new TaskSession { Id = "fixture-reveal", ProfileId = "lead",
                    Title = "Before automatic rename", ProjectId = "book", UpdatedAt = now.AddDays(-4) };
                navigationSessions.Add(revealSession);
                selectedNavigationId = "conversation-fixture-reveal";
                selectedNavigationOccurrence = null;
                revealedNavigation = null;
                pendingNavigationReveal = null;
                suppressedNavigationRevealId = null;
                RenderNavigation();
                revealSession.Title = "After automatic rename";
                RenderNavigation();
                Check(revealedNavigation is null,
                    "A replaced deferred callback must not count as a completed row reveal.");
                container.UpdateLayout();
                var navigationScroller = NavigationDescendant<ScrollViewer>(navigationTree) ??
                    throw new InvalidOperationException("Navigation scroll viewer is missing.");
                navigationScroller.ScrollToVerticalOffset(0);
                navigationScroller.UpdateLayout();
                Dispatcher.Invoke(new Action(() => { }), System.Windows.Threading.DispatcherPriority.ContextIdle);
                container.UpdateLayout();
                var revealedRow = Find("Navigation-conversation-fixture-reveal");
                Check(revealedRow.IsSelected && selectedNavigationId == "conversation-fixture-reveal" &&
                    NavigationRowVisible(revealedRow, navigationScroller) &&
                    revealedNavigation is { Id: "conversation-fixture-reveal", Title: "After automatic rename" },
                    "A superseding rename render must reveal the selected saved row after the older callback is cancelled.");
                revealSession.Title = "First later rename";
                RenderNavigation();
                revealSession.Title = "Second later rename";
                RenderNavigation();
                Check(pendingNavigationReveal is { Id: "conversation-fixture-reveal", Title: "Second later rename" },
                    "A replaced metadata callback must keep its pending reveal after an earlier reveal completed.");
                container.UpdateLayout();
                navigationScroller.ScrollToVerticalOffset(0);
                navigationScroller.UpdateLayout();
                Dispatcher.Invoke(new Action(() => { }), System.Windows.Threading.DispatcherPriority.ContextIdle);
                Check(NavigationRowVisible(Find("Navigation-conversation-fixture-reveal"), navigationScroller) &&
                    revealedNavigation is { Id: "conversation-fixture-reveal", Title: "Second later rename" } &&
                    pendingNavigationReveal is null,
                    "A superseding metadata refresh must complete the actual selected-row reveal.");
                navigationScroller.ScrollToVerticalOffset(0);
                navigationScroller.UpdateLayout();
                RenderNavigation();
                container.UpdateLayout();
                Dispatcher.Invoke(new Action(() => { }), System.Windows.Threading.DispatcherPriority.ContextIdle);
                Check(navigationScroller.VerticalOffset < 1 &&
                    !NavigationRowVisible(Find("Navigation-conversation-fixture-reveal"), navigationScroller),
                    "An unchanged completed reveal must preserve deliberate browsing instead of scrolling back to the selection.");
                revealSession.Title = "Background rename while browsing";
                RenderNavigation();
                container.UpdateLayout();
                Dispatcher.Invoke(new Action(() => { }), System.Windows.Threading.DispatcherPriority.ContextIdle);
                Check(navigationScroller.VerticalOffset < 1 &&
                    !NavigationRowVisible(Find("Navigation-conversation-fixture-reveal"), navigationScroller),
                    "A metadata refresh must not bring an already revealed selection back after deliberate browsing.");
                var groupedChat = CurrentConversationId; var groupedDraft = prompt.Text;
                Find("NavigationSection.Unassigned").IsSelected = true;
                Find("NavigationSection.Unassigned").IsExpanded = false;
                navigationScroller.ScrollToVerticalOffset(Math.Min(8, navigationScroller.ScrollableHeight)); navigationScroller.UpdateLayout();
                var groupedOffset = navigationScroller.VerticalOffset;
                Check(groupedOffset > 0 && selectedNavigationId is null,
                    "The selected virtual-folder scroll fixture establishes a real nonzero navigation position.");
                RenderNavigation(); container.UpdateLayout();
                Dispatcher.Invoke(new Action(() => { }), System.Windows.Threading.DispatcherPriority.ContextIdle);
                Check(Math.Abs(navigationScroller.VerticalOffset - groupedOffset) <= .1 &&
                    Find("NavigationSection.Unassigned").IsSelected && !Find("NavigationSection.Unassigned").IsExpanded &&
                    CurrentConversationId == groupedChat && prompt.Text == groupedDraft,
                    "A passive projection refresh preserves the selected collapsed Unassigned group, nonzero scroll position and current draft.");
                revealedNavigation = null;
                RenderNavigation();
                selectedNavigationId = "conversation-recent";
                selectedNavigationOccurrence = null;
                RenderNavigation();
                container.UpdateLayout();
                navigationScroller.ScrollToVerticalOffset(0);
                navigationScroller.UpdateLayout();
                Dispatcher.Invoke(new Action(() => { }), System.Windows.Threading.DispatcherPriority.ContextIdle);
                Check((navigationTree.SelectedItem as TreeViewItem)?.Tag is NavigationNode { Id: "conversation-recent" } &&
                    revealedNavigation is { Id: "conversation-recent" } &&
                    NavigationRowVisible(Find("Recent-conversation-recent"), navigationScroller),
                    "A stale deferred reveal must not scroll to an earlier row or steal the replacement selection.");
            }
            finally
            {
                navigationSessions = savedSessions;
                selectedNavigationId = savedNavigationId;
                selectedNavigationOccurrence = savedOccurrence;
                revealedNavigation = savedReveal;
                pendingNavigationReveal = savedPendingReveal;
                suppressedNavigationRevealId = savedSuppressedRevealId;
                RenderNavigation();
                container.UpdateLayout();
                Dispatcher.Invoke(new Action(() => { }), System.Windows.Threading.DispatcherPriority.ContextIdle);
            }
            ResetRails();
            container.Measure(new Size(1280, 840)); container.Arrange(new Rect(0, 0, 1280, 840)); container.UpdateLayout();
            UpdateConversationLayout(); container.UpdateLayout();
            var workspace = workspaceRail ?? throw new InvalidOperationException("Workspace rail was not constructed.");
            var activity = activityRail ?? throw new InvalidOperationException("Activity rail was not constructed.");
            var center = conversationCenter ?? throw new InvalidOperationException("Conversation center was not constructed.");
            var headerStrip = conversationHeaderStrip ?? throw new InvalidOperationException("Conversation header strip was not constructed.");
            Check(workspace.Visibility == Visibility.Visible && activity.Visibility == Visibility.Collapsed && center.ActualWidth >= 850,
                "At 1280 DIPs the optional right sidebar must stay collapsed while the workspace and conversation retain useful width.");
            Check(activityVisibilityButton?.Visibility == Visibility.Visible && AutomationProperties.GetAutomationId(activityVisibilityButton) == "ToggleActivitySidebar",
                "The collapsed right sidebar must retain an obvious accessible Sidebar control.");
            Check(desktopSidebarTabs.Items.Count == 0 &&
                  AutomationProperties.GetAutomationId(desktopSidebarTabs) == "DesktopSidebarTabs" &&
                  !Tabs.Items.OfType<TabItem>().Any(x => Equals(x.Header, "Plugins")),
                "Turn activity must stay inline while the empty right host remains reserved for plugin panels without creating an application-level Plugins tab.");
            var panelsRequested = activityRequested;
            activityRequested = true; UpdateConversationLayout(); container.UpdateLayout();
            Check(activityRequested && activity.Visibility == Visibility.Collapsed && activityColumn!.ActualWidth == 0 &&
                activityVisibilityButton?.IsEnabled == false &&
                Equals(activityVisibilityButton.ToolTip, "No plugin panels are available."),
                "An empty plugin rail reserves no column and disables Panels without losing its latent visibility setting.");
            activityRequested = panelsRequested; UpdateConversationLayout(); container.UpdateLayout();
            Check(headerStrip.ActualHeight <= 48 && agentSummary.Visibility == Visibility.Collapsed && projectKnowledgeControls?.Visibility == Visibility.Visible,
                $"Conversation title, state, project and actions must share one compact line; model details belong in its menu; actual height {headerStrip.ActualHeight:F2} DIPs.");
            Check(compactActionsButton?.Visibility == Visibility.Visible && AutomationProperties.GetAutomationId(compactActionsButton) == "ConversationActions",
                "The compact strip must keep a stable Actions overflow entry at every size.");
            var menuIds = ConversationActionsMenu().Items.OfType<MenuItem>().Select(AutomationProperties.GetAutomationId).ToHashSet(StringComparer.Ordinal);
            Check(new[] { "ToggleActivitySidebarMenuItem", "RefreshConfigurationMenuItem", "CompactConversationMenuItem", "RetryConnectionMenuItem", "ChangeConversationProject", "OpenKnowledgeSettings" }.All(menuIds.Contains),
                "The overflow menu must retain sidebar, configuration, compact, retry, project and knowledge actions.");
            fixtureSidebarTab = new TabItem { Header = "Controlled plugin panel", Content = new TextBlock { Text = "Synthetic plugin sidebar content." } };
            desktopSidebarTabs.Items.Add(fixtureSidebarTab); container.UpdateLayout();
            Check(activityVisibilityButton?.IsEnabled == true,
                "A real plugin-tab collection change enables the existing Panels control without an owner query.");
            var mediumLayoutWidth = conversationLayout?.ActualWidth ?? 0;
            var mediumHeaderHeight = headerStrip.ActualHeight;
            var mediumCenterWidth = center.ActualWidth;
            var mediumTranscriptHeight = transcript.ActualHeight;
            var mediumPromptHeight = prompt.ActualHeight;

            container.Measure(new Size(1024, 768)); container.Arrange(new Rect(0, 0, 1024, 768)); container.UpdateLayout();
            UpdateConversationLayout(); container.UpdateLayout();
            Check(workspace.Visibility == Visibility.Visible && activity.Visibility == Visibility.Collapsed && center.ActualWidth >= 620,
                $"At 1024x768 the conversation must keep Workspace available, collapse the optional right sidebar, and retain a useful center; center={center.ActualWidth:F2} DIPs.");
            Check(transcript.ActualHeight >= 300 && prompt.ActualHeight <= 100,
                $"At 1024x768 the transcript must remain primary and the composer bounded; transcript={transcript.ActualHeight:F2}, prompt={prompt.ActualHeight:F2} DIPs.");

            container.Measure(new Size(720, 516)); container.Arrange(new Rect(0, 0, 720, 516)); container.UpdateLayout();
            UpdateConversationLayout(); container.UpdateLayout();
            Check(workspace.Visibility == Visibility.Collapsed && activity.Visibility == Visibility.Collapsed &&
                center.ActualWidth >= 500,
                "A constrained high-DPI-sized window must collapse both side rails and preserve a useful conversation width.");
            var smallHeader = Bounds(headerStrip, container);
            var smallActions = Bounds(compactActionsButton!, container);
            Check(smallHeader.Height <= 48 && smallActions.Right <= smallHeader.Right && smallActions.Bottom <= smallHeader.Bottom &&
                agentSummary.Visibility == Visibility.Collapsed && projectKnowledgeControls?.Visibility == Visibility.Collapsed && sessionState.Visibility == Visibility.Visible,
                "A constrained-height window must keep status and actions in one bounded line, hiding secondary metadata instead of wrapping or overlapping.");
            Check(transcript.ActualHeight >= 130 && prompt.ActualHeight <= 64,
                $"A constrained-height window must preserve a useful transcript and compact composer; transcript={transcript.ActualHeight:F2}, prompt={prompt.ActualHeight:F2} DIPs.");
            Check(workspaceVisibilityButton?.Visibility == Visibility.Visible && activityVisibilityButton?.Visibility == Visibility.Visible,
                "At the narrowest supported width both rail toggles remain reachable in the compact application row.");
            menuIds = ConversationActionsMenu().Items.OfType<MenuItem>().Select(AutomationProperties.GetAutomationId).ToHashSet(StringComparer.Ordinal);
            Check(menuIds.Contains("ToggleWorkspaceSidebarMenuItem") && menuIds.Contains("ToggleActivitySidebarMenuItem"),
                "The narrow overflow menu must retain both optional sidebar controls.");
            var smallLayoutWidth = conversationLayout?.ActualWidth ?? 0;
            var smallHeaderHeight = headerStrip.ActualHeight;
            var smallCenterWidth = center.ActualWidth;
            var smallTranscriptHeight = transcript.ActualHeight;
            var smallPromptHeight = prompt.ActualHeight;
            Check(transcript.HorizontalScrollBarVisibility == ScrollBarVisibility.Disabled && transcript.PanningMode == PanningMode.VerticalOnly,
                "The conversation keeps message cards at the viewport width and owns one vertical scroll path.");
            workspaceRequested = true; compactWorkspaceOpen = true; UpdateConversationLayout(); container.UpdateLayout();
            Check(workspace.Visibility == Visibility.Visible && activity.Visibility == Visibility.Collapsed,
                "A user must be able to reveal one compact rail without squeezing in the other rail.");
            Check(transcript.ActualHeight >= 130,
                $"Opening one compact rail must not consume the transcript's usable vertical space; transcript={transcript.ActualHeight:F2}, header={headerStrip.ActualHeight:F2}, composer={conversationComposer?.ActualHeight:F2}, prompt={prompt.ActualHeight:F2}, children={string.Join(",", conversationComposer?.Children.OfType<FrameworkElement>().Select(x => x.GetType().Name + ":" + x.ActualHeight.ToString("F0") + ":" + x.Visibility) ?? [])} DIPs.");
            ResetRails();
            container.Measure(new Size(1280, 840)); container.Arrange(new Rect(0, 0, 1280, 840)); container.UpdateLayout();
            UpdateConversationLayout(); container.UpdateLayout();
            ToggleCompactRail("activity"); container.UpdateLayout();
            Check(workspace.Visibility == Visibility.Collapsed && activity.Visibility == Visibility.Visible && center.ActualWidth >= 900,
                $"Revealing the right sidebar below its automatic threshold must swap out Workspace instead of crowding the conversation; workspace={workspace.Visibility}, sidebar={activity.Visibility}, center={center.ActualWidth:F2} DIPs.");
            ToggleCompactRail("activity"); container.UpdateLayout();
            Check(activity.Visibility == Visibility.Collapsed && workspace.Visibility == Visibility.Visible,
                "The same Sidebar control must close the optional rail and restore the normal workspace layout.");

            ResetRails();
            container.Measure(new Size(1440, 960)); container.Arrange(new Rect(0, 0, 1440, 960)); container.UpdateLayout();
            UpdateConversationLayout(); container.UpdateLayout();
            Check(workspace.Visibility == Visibility.Visible && activity.Visibility == Visibility.Collapsed,
                "Increasing width must not open a plugin panel the user did not request.");
            ToggleCompactRail("activity"); container.UpdateLayout();
            Check(workspace.Visibility == Visibility.Visible && activity.Visibility == Visibility.Visible && center.ActualWidth >= 700,
                "An explicitly opened plugin panel can share a wide workspace without starving the conversation.");
            ToggleCompactRail("activity"); container.UpdateLayout();
            Check(activity.Visibility == Visibility.Collapsed, "Closing the plugin panel remains effective at wide sizes.");
            // Exercise the native splitter's resizing path and the host's drag-completion capture.
            // This is an offscreen WPF regression, not an actual mouse/active-window acceptance.
            var originalTitle = navigationSessions[1].Title;
            var longTitle = "Prepare locations, resolve the remaining story questions, and plan the next chapter review";
            navigationSessions[1].Title = longTitle;
            RenderNavigation(); container.UpdateLayout();
            static TextBlock TitleLabel(TreeViewItem item) =>
                ((StackPanel)((Grid)item.Header).Children[1]).Children.OfType<TextBlock>().First();
            var title = TitleLabel(Find("Recent-conversation-recent"));
            var narrowTitleHeight = title.ActualHeight;
            Check(narrowTitleHeight is > 0 and <= 24 && title.TextWrapping == TextWrapping.NoWrap &&
                  title.TextTrimming == TextTrimming.CharacterEllipsis && title.Text == longTitle && Equals(title.ToolTip, longTitle) &&
                  Bounds(title, Find("Recent-conversation-recent")).Right <= Find("Recent-conversation-recent").ActualWidth + 1,
                $"A long conversation title stays in one clipped row with ellipsis and its complete unchanged text and tooltip: height={narrowTitleHeight:F1}, width={title.ActualWidth:F1}, text={title.Text}, tooltip={title.ToolTip}, rail={workspace.ActualWidth:F1}, item={Find("Recent-conversation-recent").ActualWidth:F1}.");

            var appearance = new MenuItem(); AddSidebarAppearanceItems(appearance);
            var wrapOption = appearance.Items.OfType<MenuItem>().Single(item =>
                AutomationProperties.GetAutomationId(item) == "ApplicationAppearance.WrapChatNames");
            var wrapDraft = prompt.Text; var wrapChat = CurrentConversationId; var wrapVisits = conversationHistory.Current;
            wrapOption.IsChecked = true; wrapOption.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)); container.UpdateLayout();
            title = TitleLabel(Find("Recent-conversation-recent"));
            Check(wrapChatNames && title.TextWrapping == TextWrapping.Wrap && title.TextTrimming == TextTrimming.None &&
                title.ActualHeight > narrowTitleHeight && Equals(title.ToolTip, longTitle) && navigationSessions[1].Title == longTitle &&
                prompt.Text == wrapDraft && CurrentConversationId == wrapChat && conversationHistory.Current == wrapVisits && !IsLoaded && !IsVisible,
                "The actual Appearance wrap option changes only local rendering in the unshown fixture, preserving title, chat, draft and history.");
            wrapOption.IsChecked = false; wrapOption.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)); container.UpdateLayout();
            navigationSessions[1].Title = longTitle + "\nA second title line";
            RenderNavigation(); container.UpdateLayout(); title = TitleLabel(Find("Recent-conversation-recent"));
            Check(!wrapChatNames && title.TextWrapping == TextWrapping.NoWrap && title.ActualHeight is > 0 and <= 24 &&
                !title.Text.Contains('\n') && Equals(title.ToolTip, navigationSessions[1].Title) &&
                navigationSessions[1].Title.EndsWith("\nA second title line", StringComparison.Ordinal),
                "Default one-line labels normalize only their visible whitespace and retain the exact multiline stored title and tooltip.");
            navigationSessions[1].Title = longTitle; RenderNavigation(); container.UpdateLayout();
            title = TitleLabel(Find("Recent-conversation-recent"));

            var splitter = workspaceDivider ?? throw new InvalidOperationException("Workspace divider was not constructed.");
            var transform = (ITransformProvider)new GridSplitterAutomationPeer(splitter).GetPattern(PatternInterface.Transform);
            var beforeResize = workspaceColumn!.ActualWidth;
            transform.Move(400, 0); container.UpdateLayout();
            splitter.RaiseEvent(new DragCompletedEventArgs(400, 0, false) { RoutedEvent = Thumb.DragCompletedEvent });
            container.UpdateLayout();
            Check(workspaceColumn.ActualWidth >= beforeResize + 390 && workspaceColumn.ActualWidth > 600 &&
                  center.ActualWidth >= MinimumConversationWidth && Bounds(center, container).Right <= container.ActualWidth,
                "The real splitter must widen the title pane beyond the former cap without pushing the conversation outside its parent.");
            Check(Math.Abs(title.ActualHeight - narrowTitleHeight) < 1 && title.ActualWidth > 400 &&
                  title.Text == longTitle && Equals(title.ToolTip, longTitle),
                "Widening the sidebar reveals more title text without changing its single-row height or stored value.");

            var preferredWidth = workspacePreferredWidth;
            container.Measure(new Size(720, 516)); container.Arrange(new Rect(0, 0, 720, 516));
            compactWorkspaceOpen = true; UpdateConversationLayout(); container.UpdateLayout();
            Check(workspace.Visibility == Visibility.Visible && center.ActualWidth >= MinimumConversationWidth &&
                  Bounds(center, container).Right <= container.ActualWidth && workspacePreferredWidth == preferredWidth,
                $"A smaller parent must constrain the actual sidebar while retaining the user's preferred width and usable composer: workspace={workspace.Visibility}/{workspaceColumn.ActualWidth:F1}, center={center.ActualWidth:F1}, right={Bounds(center, container).Right:F1}, container={container.ActualWidth:F1}, layout={conversationLayout!.ActualWidth:F1}, slot={LayoutInformation.GetLayoutSlot(conversationLayout).Width:F1}, preferred={workspacePreferredWidth:F1}/{preferredWidth:F1}.");
            container.Measure(new Size(1440, 960)); container.Arrange(new Rect(0, 0, 1440, 960));
            UpdateConversationLayout(); container.UpdateLayout();
            Check(Math.Abs(workspaceColumn.ActualWidth - preferredWidth) < 1,
                "Growing the parent again must restore the sidebar width selected through its splitter.");
            activityRequested = true; UpdateConversationLayout(); container.UpdateLayout();
            Check(center.ActualWidth >= MinimumConversationWidth && Bounds(center, container).Right <= container.ActualWidth,
                "Opening the existing plugin panel must respect the same conversation minimum and parent boundary.");
            transform.Move(10_000, 0); container.UpdateLayout();
            splitter.RaiseEvent(new DragCompletedEventArgs(10_000, 0, false) { RoutedEvent = Thumb.DragCompletedEvent });
            container.UpdateLayout();
            Check(center.ActualWidth >= MinimumConversationWidth - 1 &&
                  Bounds(center, container).Right <= container.ActualWidth + 1,
                "Dragging beyond available space must stop at the visible panes and conversation minimum.");
            activityRequested = false; UpdateConversationLayout(); container.UpdateLayout();

            navigationSessions[1].Title = "Renamed conversation after title generation";
            RenderNavigation(); container.UpdateLayout();
            title = TitleLabel(Find("Recent-conversation-recent"));
            Check(title.Text == navigationSessions[1].Title && Equals(title.ToolTip, navigationSessions[1].Title) &&
                  (navigationTree.SelectedItem as TreeViewItem)?.Tag is NavigationNode { Id: "conversation-recent" },
                "A refreshed title must update its text and full tooltip while preserving the selected conversation.");
            navigationSessions[1].Title = originalTitle; workspacePreferredWidth = 248; compactWorkspaceOpen = false;
            RenderNavigation(); UpdateConversationLayout(); container.UpdateLayout();

            if (previewPath is not null)
            {
                if (!Path.IsPathFullyQualified(previewPath) || !previewPath.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
                    throw new ArgumentException("Preview needs an absolute PNG path.");
                var previewSessions = navigationSessions;
                var previewWrapping = wrapChatNames; var previewCount = sidebarRecentCount;
                var previewSections = collapsedNavigationSections.ToArray();
                try
                {
                navigationSessions = [.. navigationSessions,
                    new() { Id = "sidebar-preview-four", ProfileId = "lead",
                        Title = "Discuss revision priorities and outstanding questions for the next chapter review", UpdatedAt = now.AddMinutes(-1) },
                    new() { Id = "sidebar-preview-five", ProfileId = "lead",
                        Title = "Plan the follow-up research and compare the available approaches", UpdatedAt = now.AddMinutes(-2) }];
                sidebarRecentCount = 5; wrapChatNames = false;
                collapsedNavigationSections.Add("Unassigned"); collapsedNavigationSections.Remove("Recent");
                RenderNavigation();
                Check(Find("NavigationSection.Recent").Items.Count == 5 && CanonicalChats().Length == 5 &&
                    !Find("NavigationSection.Unassigned").IsExpanded &&
                    Find("Navigation-series").Parent == Find("Navigation-writing") && Find("Navigation-book").Parent == Find("Navigation-series") &&
                    Find("NavigationSection.Recent").Items.OfType<TreeViewItem>().All(row =>
                        TitleLabel(row) is { TextWrapping: TextWrapping.NoWrap, TextTrimming: TextTrimming.CharacterEllipsis } label &&
                        Equals(label.ToolTip, ((NavigationNode)row.Tag).Title)),
                    "The controlled previews contain Recent5, the real nested folder tree and collapsed Unassigned, with exact full title tooltips.");
                var smallPreviewPath = Path.Combine(Path.GetDirectoryName(previewPath)!, Path.GetFileNameWithoutExtension(previewPath) + ".small-150.png");
                ResetRails(); compactWorkspaceOpen = true;
                container.Measure(new Size(720, 516)); container.Arrange(new Rect(0, 0, 720, 516)); container.UpdateLayout(); UpdateConversationLayout(); container.UpdateLayout();
                var smallBitmap = new RenderTargetBitmap(1080, 774, 144, 144, PixelFormats.Pbgra32);
                var smallBackdrop = new DrawingVisual();
                using (var drawing = smallBackdrop.RenderOpen()) drawing.DrawRectangle(Background, null, new Rect(0, 0, 720, 516));
                smallBitmap.Render(smallBackdrop); smallBitmap.Render(container);
                var smallEncoder = new PngBitmapEncoder(); smallEncoder.Frames.Add(BitmapFrame.Create(smallBitmap));
                using (var smallOutput = new FileStream(smallPreviewPath, FileMode.Create, FileAccess.Write)) smallEncoder.Save(smallOutput);

                container.Measure(new Size(1280, 840)); container.Arrange(new Rect(0, 0, 1280, 840)); container.UpdateLayout(); UpdateConversationLayout(); container.UpdateLayout();
                var bitmap = new RenderTargetBitmap(1280, 840, 96, 96, PixelFormats.Pbgra32);
                var backdrop = new DrawingVisual();
                using (var drawing = backdrop.RenderOpen()) drawing.DrawRectangle(Background, null, new Rect(0, 0, 1280, 840));
                bitmap.Render(backdrop); bitmap.Render(container);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using (var output = new FileStream(previewPath, FileMode.Create, FileAccess.Write)) encoder.Save(output);
                wrapChatNames = true; RenderNavigation(); container.UpdateLayout();
                Check(Find("NavigationSection.Recent").Items.OfType<TreeViewItem>().All(row =>
                    TitleLabel(row).TextWrapping == TextWrapping.Wrap && Equals(TitleLabel(row).ToolTip, ((NavigationNode)row.Tag).Title)),
                    "The optional controlled wrap preview retains the same exact chat titles and IDs.");
                var wrapBitmap = new RenderTargetBitmap(1280, 840, 96, 96, PixelFormats.Pbgra32);
                wrapBitmap.Render(backdrop); wrapBitmap.Render(container);
                var wrapEncoder = new PngBitmapEncoder(); wrapEncoder.Frames.Add(BitmapFrame.Create(wrapBitmap));
                var wrapPath = Path.Combine(Path.GetDirectoryName(previewPath)!, Path.GetFileNameWithoutExtension(previewPath) + ".wrap.png");
                using (var output = new FileStream(wrapPath, FileMode.Create, FileAccess.Write)) wrapEncoder.Save(output);
                }
                finally
                {
                    navigationSessions = previewSessions; wrapChatNames = previewWrapping; sidebarRecentCount = previewCount;
                    collapsedNavigationSections.Clear(); foreach (var section in previewSections) collapsedNavigationSections.Add(section);
                    RenderNavigation(); container.UpdateLayout();
                }
            }
            // Exercise the actual hidden-window local reopen path with two nonempty drafts.
            // This catches TextChanged deleting the restored ID before its text is read.
            var choiceA = new FreshChatSelection("lead", profiles[0].Revision, []);
            var choiceB = new FreshChatSelection(null, null, []);
            NewConversationInProject("book", choiceA);
            var draftA = CurrentConversationId!;
            prompt.Text = "First unsent project draft"; selectedResponseSharePercent = 25;
            conversationHistory.Clear();
            conversationHistory.Record(draftA, "book", ServiceConnectionIdentity, true, false,
                new LocalDraftVisitState(choiceA, 25));
            NewConversationInProject(null, choiceB);
            var draftB = CurrentConversationId!;
            prompt.Text = "Second unsent general draft"; selectedResponseSharePercent = 30;
            conversationHistory.Record(draftB, null, ServiceConnectionIdentity, true, false,
                new LocalDraftVisitState(choiceB, 30));
            var pluginFrame = new Border();
            AutomationProperties.SetAutomationId(pluginFrame, "PluginNewConversationStart-fixture");
            using var childHost = new System.Windows.Forms.Integration.WindowsFormsHost();
            Check(CanRouteConversationHistoryMouse(prompt, MouseButton.XButton1) &&
                !CanRouteConversationHistoryMouse(prompt, MouseButton.XButton2) &&
                !CanRouteConversationHistoryMouse(pluginFrame, MouseButton.XButton1) &&
                !CanRouteConversationHistoryMouse(desktopWorkspaceTrees, MouseButton.XButton1) &&
                !CanRouteConversationHistoryMouse(desktopPluginAccessories, MouseButton.XButton1) &&
                !CanRouteConversationHistoryMouse(childHost, MouseButton.XButton1) &&
                !CanRouteConversationHistoryMouse(prompt, MouseButton.Left),
                "Main side buttons must route only available history directions outside plugin and child-HWND surfaces.");
            Check(ConfirmedConversationAbsent(new InvalidOperationException("Conversation was not found."), false) &&
                ConfirmedConversationAbsent(new InvalidOperationException("Session not found."), true) &&
                !ConfirmedConversationAbsent(new InvalidOperationException("Session not found."), false) &&
                !ConfirmedConversationAbsent(new InvalidOperationException("Service unavailable."), false),
                "Only an exact owner not-found reply may remove a history entry; transient failures stay retryable.");
            var semanticInstance = Guid.NewGuid().ToString("N");
            var semanticRequest = 0;
            var attachedOperations = DesktopControlServer.ModeCapabilities(false, true)
                .GetProperty("admittedOperations").EnumerateArray().Select(item => item.GetString()).ToArray();
            var fullOperations = DesktopControlServer.ModeCapabilities(false, false)
                .GetProperty("admittedOperations").EnumerateArray().Select(item => item.GetString()).ToArray();
            Check(attachedOperations.Contains("conversation.history.back") &&
                attachedOperations.Contains("conversation.history.forward") &&
                !fullOperations.Contains("conversation.history.back") &&
                !fullOperations.Contains("conversation.history.forward"),
                "History controls must be discoverable only through exact-selection semantic attach.");
            DesktopControlResponse HistoryCall(string operation, object arguments) =>
                HandleSemanticAttachControlAsync(new DesktopControlRequest(semanticInstance,
                    "navigation-fixture-" + ++semanticRequest, operation, CerebralJson.Element(arguments)),
                    CancellationToken.None).GetAwaiter().GetResult();
            var beforeHistory = CurrentConversationId;
            try
            {
                _ = HistoryCall("conversation.history.back", new { });
                throw new InvalidOperationException("History admitted a missing expected selection.");
            }
            catch (ArgumentException) { checks++; }
            var staleSelection = new
            {
                conversationId = CurrentConversationId, projectId = ConversationProjectId,
                selectionVersion = conversationSelectionVersion + 1,
                connectionVersion = knowledgeConnectionVersion, connectionIdentity = ServiceConnectionIdentity
            };
            try
            {
                _ = HistoryCall("conversation.history.back", new { expected = staleSelection });
                throw new InvalidOperationException("History admitted a stale expected selection.");
            }
            catch (InvalidOperationException error) when (error.Message.Contains("semantic attach selection changed", StringComparison.Ordinal)) { checks++; }
            var staleConnection = new
            {
                conversationId = CurrentConversationId, projectId = ConversationProjectId,
                selectionVersion = conversationSelectionVersion,
                connectionVersion = knowledgeConnectionVersion + 1, connectionIdentity = ServiceConnectionIdentity
            };
            try
            {
                _ = HistoryCall("conversation.history.back", new { expected = staleConnection });
                throw new InvalidOperationException("History admitted an earlier connection scope.");
            }
            catch (InvalidOperationException error) when (error.Message.Contains("semantic attach connection changed", StringComparison.Ordinal)) { checks++; }
            Check(CurrentConversationId == beforeHistory && conversationHistory.Current?.ConversationId == draftB,
                "Missing or stale semantic selection must not navigate or move the history cursor.");
            var backResult = HistoryCall("conversation.history.back", new { expected = SemanticAttachSelection() });
            Check(backResult.State == "completed" && backResult.Data.GetProperty("navigation")
                .GetProperty("history").GetProperty("index").GetInt32() == 0,
                "Guarded semantic Back must return a committed cursor in the existing snapshot shape.");
            Check(conversationHistory.Current?.ConversationId == draftA &&
                CurrentConversationId == draftA && prompt.Text == "First unsent project draft" &&
                draftProjectId == "book" && selectedResponseSharePercent == 25 &&
                freshChatDraft?.Selection.ProfileId == "lead" &&
                freshChatDraft.Selection.ProfileRevision == profiles[0].Revision &&
                drafts[draftB] == "Second unsent general draft",
                "Back must reopen the exact first unsent ID, project, text and profile without deleting the second draft.");
            Check(CanRouteConversationHistoryMouse(prompt, MouseButton.XButton2),
                "Forward mouse routing becomes available after Back commits its cursor.");
            var forwardResult = HistoryCall("conversation.history.forward", new { expected = SemanticAttachSelection() });
            Check(forwardResult.State == "completed" && forwardResult.Data.GetProperty("navigation")
                .GetProperty("history").GetProperty("index").GetInt32() == 1,
                "Guarded semantic Forward must return the newly committed cursor.");
            Check(conversationHistory.Current?.ConversationId == draftB &&
                CurrentConversationId == draftB && prompt.Text == "Second unsent general draft" &&
                draftProjectId is null && selectedResponseSharePercent == 30 &&
                freshChatDraft?.Selection.ProfileId is null &&
                drafts[draftA] == "First unsent project draft",
                "Forward must reopen the exact second unsent ID and preserve both drafts independently.");
            _ = HistoryCall("conversation.history.back", new { expected = SemanticAttachSelection() });
            var boundary = HistoryCall("conversation.history.back", new { expected = SemanticAttachSelection() });
            Check(boundary.State == "rejected" && boundary.Data.GetProperty("code").GetString() == "history_boundary" &&
                conversationHistory.Current?.ConversationId == draftA && prompt.Text == "First unsent project draft",
                "Known Back boundary must report a closed no-op without changing the selected draft.");
            Console.WriteLine(JsonSerializer.Serialize(new { success = true, navigationChecks = checks,
                responsive = new
                {
                    activityAutomaticWidth = ActivitySidebarAutomaticWidth,
                    medium1280 = new { layoutWidth = Rounded(mediumLayoutWidth), headerHeight = Rounded(mediumHeaderHeight), centerWidth = Rounded(mediumCenterWidth), transcriptHeight = Rounded(mediumTranscriptHeight), promptHeight = Rounded(mediumPromptHeight), workspaceVisible = true, activityVisible = false },
                    scaledSmall720 = new { layoutWidth = Rounded(smallLayoutWidth), headerHeight = Rounded(smallHeaderHeight), centerWidth = Rounded(smallCenterWidth), transcriptHeight = Rounded(smallTranscriptHeight), promptHeight = Rounded(smallPromptHeight), dpi = 144, pixels = "1080x774", secondaryContextVisible = false }
                },
                serviceCommandsExecuted = false, projectFilesCreated = false, windowShown = false, previewPath }));
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(JsonSerializer.Serialize(new { success = false, navigationChecks = checks, error = ex.ToString() })); return 1; }
        finally { if (fixtureSidebarTab is not null) desktopSidebarTabs.Items.Remove(fixtureSidebarTab); Close(); }
    }
}
