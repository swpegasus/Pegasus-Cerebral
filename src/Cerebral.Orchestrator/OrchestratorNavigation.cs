using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using Cerebral.Core;
using Cerebral.Desktop;

namespace Cerebral.Orchestrator;

public sealed partial class OrchestratorWindow
{
    private readonly TreeView navigationTree = new() { BorderThickness = new Thickness(0), Background = Ui.Surface, Padding = new Thickness(0, 4, 4, 10) };
    private readonly TextBox navigationFilter = Ui.Input();
    private readonly TextBlock navigationSelection = Ui.Text("Select a project or conversation to organize it.", 11, Ui.Muted);
    private readonly TextBlock navigationRepository = Ui.Text("", 11, Ui.Muted);
    private Button? pinNavigationButton;
    private Button? moreNavigationButton;
    private Button? backNavigationButton;
    private Button? forwardNavigationButton;
    private WrapPanel? workspaceActionRow;
    private NavigationState navigation = new();
    private List<TaskSession> navigationSessions = [];
    private readonly HashSet<string> expandedNavigation = new(StringComparer.Ordinal);
    private readonly HashSet<string> knownNavigation = new(StringComparer.Ordinal);
    private readonly HashSet<string> collapsedNavigationSections = new(StringComparer.Ordinal) { "Unassigned" };
    private string? selectedNavigationId;
    private string? selectedNavigationOccurrence;
    private readonly record struct NavigationRevealTarget(string Id, string Occurrence, string Title,
        string? ParentId, DateTimeOffset? Activity);
    private NavigationRevealTarget? revealedNavigation;
    private NavigationRevealTarget? pendingNavigationReveal;
    private string? suppressedNavigationRevealId;
    private int navigationRenderVersion;
    private int recentChatLimit = RecentChatPolicy.DefaultLimit;
    private bool showingAllLoadedChats;
    private const int DefaultSidebarRecentCount = 5;
    private int sidebarRecentCount = ReadSidebarRecentCount();
    private bool wrapChatNames = UiPreferenceStore.ReadBool("Cerebral.WrapChatNames", false);

    private static int ReadSidebarRecentCount() =>
        int.TryParse(UiPreferenceStore.ReadString("Cerebral.SidebarRecentCount"),
            System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var value) &&
        RecentChatPolicy.IsValid(value) ? value : DefaultSidebarRecentCount;

    private void SetSidebarRecentCount(int value)
    {
        if (!RecentChatPolicy.IsValid(value)) throw new ArgumentOutOfRangeException(nameof(value));
        sidebarRecentCount = value; RenderNavigation();
        try
        {
            if (IsLoaded && IsVisible) UiPreferenceStore.WriteString("Cerebral.SidebarRecentCount",
                value.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        catch (Exception error) { SetStatus("The Recent count is shown locally but could not be saved: " + error.Message, true); }
    }

    private void SetChatNameWrapping(bool value)
    {
        wrapChatNames = value; RenderNavigation();
        try { if (IsLoaded && IsVisible) UiPreferenceStore.WriteBool("Cerebral.WrapChatNames", value); }
        catch (Exception error) { SetStatus("Chat name wrapping changed locally but could not be saved: " + error.Message, true); }
    }

    private void AddSidebarAppearanceItems(MenuItem appearance)
    {
        appearance.Items.Add(new Separator());
        var wrap = new MenuItem { Header = "Wrap chat names", IsCheckable = true, IsChecked = wrapChatNames };
        AutomationProperties.SetAutomationId(wrap, "ApplicationAppearance.WrapChatNames");
        wrap.Click += (_, _) => SetChatNameWrapping(wrap.IsChecked);
        appearance.Items.Add(wrap);
        var recent = new MenuItem { Header = "Recent chats shown" };
        AutomationProperties.SetAutomationId(recent, "ApplicationAppearance.RecentChats");
        foreach (var count in new[] { 5, 10, 20, 50, 100, 200 })
        {
            var item = new MenuItem { Header = count.ToString(), IsCheckable = true, IsChecked = sidebarRecentCount == count };
            AutomationProperties.SetAutomationId(item, "SidebarRecentCount." + count);
            item.Click += (_, _) => SetSidebarRecentCount(count); recent.Items.Add(item);
        }
        var custom = new MenuItem { Header = "Custom…" };
        AutomationProperties.SetAutomationId(custom, "SidebarRecentCount.Custom");
        custom.Click += (_, _) =>
        {
            var form = new FieldsDialog(this, "Recent chats", "Choose how many chats appear in Recent. Other chats remain in their folders.") { Height = 280 };
            form.Add("count", "Chats in Recent (1–200)", sidebarRecentCount.ToString());
            if (form.ShowDialog() != true) return;
            if (int.TryParse(form.Value("count").Trim(), out var count) && RecentChatPolicy.IsValid(count)) SetSidebarRecentCount(count);
            else SetStatus("Enter a Recent count from 1 to 200.", true);
        };
        recent.Items.Add(custom); appearance.Items.Add(recent);
    }

    private UIElement NavigationView()
    {
        var root = new DockPanel { Margin = new Thickness(0, 6, 5, 0) };
        var top = new StackPanel { Margin = new Thickness(0, 0, 4, 0) };
        navigationFilter.ToolTip = "Search chats by name or project. Chat actions are in the application menu or the chat's context menu.";
        navigationFilter.Height = 30; navigationFilter.MinHeight = 0;
        navigationFilter.AcceptsReturn = false; navigationFilter.TextWrapping = TextWrapping.NoWrap;
        navigationFilter.VerticalContentAlignment = VerticalAlignment.Center;
        AutomationProperties.SetAutomationId(navigationFilter, "NavigationSearch");
        AutomationProperties.SetName(navigationFilter, "Search chats");
        navigationFilter.TextChanged += (_, _) => RenderNavigation();
        navigationFilter.KeyDown += (_, e) => { if (e.Key == Key.Escape) { navigationFilter.Clear(); e.Handled = true; } };
        navigationFilter.Margin = new Thickness(0);
        var search = new Grid { Margin = new Thickness(0, 0, 0, 9) };
        var placeholder = Ui.Text("Search chats", 12, Ui.Muted);
        placeholder.Margin = new Thickness(10, 0, 0, 0);
        placeholder.VerticalAlignment = VerticalAlignment.Center; placeholder.IsHitTestVisible = false;
        AutomationProperties.SetAutomationId(placeholder, "NavigationSearchPlaceholder");
        void UpdateSearchPlaceholder() => placeholder.Visibility = navigationFilter.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        navigationFilter.TextChanged += (_, _) => UpdateSearchPlaceholder();
        search.Children.Add(navigationFilter); search.Children.Add(placeholder); UpdateSearchPlaceholder(); top.Children.Add(search);
        var newConversation = NavigationButton("New chat", "NewConversation", NewConversation);
        newConversation.Background = Ui.Accent; newConversation.Foreground = Ui.OnAccent; newConversation.BorderBrush = Ui.Accent;
        newConversation.ToolTip = "Start a chat in the selected project. You always talk to Cerebral.";
        backNavigationButton = NavigationButton("←", "ConversationBack", () => Run(() => NavigateConversationHistoryAsync(back: true)));
        forwardNavigationButton = NavigationButton("→", "ConversationForward", () => Run(() => NavigateConversationHistoryAsync(back: false)));
        AutomationProperties.SetName(backNavigationButton, "Back to previous chat");
        AutomationProperties.SetName(forwardNavigationButton, "Forward to next chat");
        backNavigationButton.ToolTip = "Back to previous chat (mouse Back)";
        forwardNavigationButton.ToolTip = "Forward to next chat (mouse Forward)";
        var create = NavigationButton("New project", "CreateWorkspaceProject", () => Run(() => AddNavigationNode("project", createDirectory: true)));
        var existing = NavigationButton("Add existing", "AddExistingWorkspaceProject", () => Run(() => AddNavigationNode("project")));
        var folder = NavigationButton("Folder", "CreateWorkspaceFolder", () => Run(() => AddNavigationNode("folder")));
        pinNavigationButton = NavigationButton("Pin", "PinWorkspaceItem", () =>
        {
            if (SelectedNavigation() is { } node) Run(() => ChangeNavigation(node, copy => copy.Pinned = !copy.Pinned));
        });
        moreNavigationButton = NavigationButton("More…", "WorkspaceItemActions", () =>
        {
            if (SelectedNavigation() is not { } node) return;
            var menu = NavigationMenu(node); menu.PlacementTarget = moreNavigationButton; menu.IsOpen = true;
        });
        var refresh = NavigationButton("Refresh", "RefreshWorkspace", () => Run(RefreshSessions));
        // Retain the existing button mount as the action source for compatibility plugins.
        // Only a compact overflow is presented; plugins cannot grow a permanent toolbar tower.
        workspaceActionRow = Ui.Bar(newConversation, backNavigationButton, forwardNavigationButton,
            create, existing, folder, pinNavigationButton, moreNavigationButton, refresh);
        olderChats = NavigationButton("Older chats", "OlderDurableChats", () => Run(ShowOlderChatsAsync));
        workspaceActionRow.Children.Add(olderChats);
        workspaceActionRow.Children.Add(NavigationButton("Current chats", "CurrentDurableChats", () => Run(async () => { showingLegacyNavigation = showingAllLoadedChats = false; durableNavigationVersion++; await RefreshSessions(); })));
        workspaceActionRow.Children.Add(NavigationButton("Previous chats", "PreviousLegacyChats", () => Run(async () => { showingLegacyNavigation = true; durableNavigationVersion++; if (olderChats is not null) olderChats.IsEnabled = false; await RefreshJobs(); await RefreshSessions(); SetStatus("Previous-generation conversations are shown for review. New messages use New chat."); })));
        // Keep the action mount for plugins, but present it through the application menu.
        // The left rail is reserved for search and chat rows.
        navigationSelection.Visibility = Visibility.Collapsed;
        DockPanel.SetDock(top, Dock.Top); root.Children.Add(top);
        navigationRepository.Margin = new Thickness(6, 7, 4, 5);
        navigationRepository.TextWrapping = TextWrapping.NoWrap; navigationRepository.TextTrimming = TextTrimming.CharacterEllipsis;
        navigationRepository.Visibility = Visibility.Collapsed;
        desktopWorkspaceTrees.Margin = new Thickness(0, 4, 0, 3);
        AutomationProperties.SetAutomationId(desktopWorkspaceTrees, "DesktopWorkspaceTrees");
        AutomationProperties.SetName(desktopWorkspaceTrees, "Plugin workspace tree branches");
        var itemStyle = Ui.ExtendStyle<TreeViewItem>();
        itemStyle.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch));
        itemStyle.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(2, 1, 1, 1)));
        itemStyle.Setters.Add(new Setter(FrameworkElement.MinHeightProperty, 30d));
        navigationTree.ItemContainerStyle = itemStyle;
        ScrollViewer.SetHorizontalScrollBarVisibility(navigationTree, ScrollBarVisibility.Disabled);
        ScrollViewer.SetVerticalScrollBarVisibility(navigationTree, ScrollBarVisibility.Auto);
        AutomationProperties.SetAutomationId(navigationTree, "NavigationTree");
        AutomationProperties.SetName(navigationTree, "All chats");
        navigationTree.SelectedItemChanged += (_, _) =>
        {
            if (selecting) return;
            if (navigationTree.SelectedItem is not TreeViewItem item) return;
            if (item.Tag is not NavigationNode node)
            {
                selectedNavigationId = null; selectedNavigationOccurrence = AutomationProperties.GetAutomationId(item);
                UpdateNavigationActions(); return;
            }
            selectedNavigationId = node.Id;
            selectedNavigationOccurrence = AutomationProperties.GetAutomationId(item);
            suppressedNavigationRevealId = null;
            UpdateNavigationActions();
            if (node.Kind == "conversation" && node.TargetId is not null && node.TargetId != CurrentConversationId)
                Run(() => SelectSession(node.TargetId));
        };
        navigationTree.PreviewMouseRightButtonDown += (_, e) =>
        {
            var item = NavigationAncestor<TreeViewItem>(e.OriginalSource as DependencyObject);
            if (item?.Tag is NavigationNode) { item.IsSelected = true; item.Focus(); }
        };
        root.Children.Add(navigationTree);
        UpdateNavigationActions();
        return root;
    }

    private static Button NavigationButton(string label, string id, Action action)
    {
        var button = Ui.Button(label, action);
        button.Padding = new Thickness(8, 5, 8, 5); button.MinHeight = 29; button.FontSize = 12;
        button.Margin = new Thickness(0, 0, 5, 5);
        AutomationProperties.SetAutomationId(button, id);
        return button;
    }

    private MenuItem BuildChatActionsMenu()
    {
        var actions = new MenuItem { Header = "Chats and projects" };
        foreach (var button in workspaceActionRow?.Children.OfType<Button>() ?? [])
        {
            var item = new MenuItem { Header = button.Content, IsEnabled = button.IsEnabled, ToolTip = button.ToolTip };
            AutomationProperties.SetAutomationId(item, AutomationProperties.GetAutomationId(button));
            item.Click += (_, _) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            actions.Items.Add(item);
        }
        var projects = new MenuItem { Header = "Projects" };
        foreach (var project in EffectiveNavigation().Where(node => node.Kind == "project").OrderBy(node => node.Title))
        {
            var item = new MenuItem { Header = project.Title, ToolTip = project.Path };
            var files = new MenuItem { Header = "Open files" };
            files.Click += (_, _) => Run(() => OpenNavigationProject(project));
            item.Items.Add(files);
            var newChat = new MenuItem { Header = "New chat in this project" };
            newChat.Click += (_, _) => NewConversationInProject(project.Id);
            item.Items.Add(newChat); projects.Items.Add(item);
        }
        if (projects.Items.Count > 0) actions.Items.Add(projects);
        return actions;
    }

    private NavigationNode? SelectedNavigation() => EffectiveNavigation().FirstOrDefault(x => x.Id == selectedNavigationId && !IsUnsentNavigationNode(x));

    private object? SelectedNavigationRowSnapshot()
    {
        // Read the materialized TreeView selection. A selected logical ID is not proof that its row exists.
        if (navigationTree.SelectedItem is not TreeViewItem { Tag: NavigationNode node } row) return null;
        string? projectId = node.Kind switch
        {
            "project" => node.Id,
            "conversation" when node.TargetId == CurrentConversationId => ConversationProjectId,
            "conversation" => navigationSessions.FirstOrDefault(item => item.Id == node.TargetId)?.ProjectId,
            _ => null
        };
        if (node.Kind == "folder")
        {
            var all = EffectiveNavigation();
            var visited = new HashSet<string>(StringComparer.Ordinal);
            var parentId = node.ParentId;
            while (parentId is not null && visited.Add(parentId))
            {
                var parent = all.FirstOrDefault(item => item.Id == parentId);
                if (parent?.Kind == "project") { projectId = parent.Id; break; }
                parentId = parent?.ParentId;
            }
        }
        return new
        {
            id = node.Id, kind = node.Kind, targetId = node.TargetId, parentId = node.ParentId,
            projectId, automationId = AutomationProperties.GetAutomationId(row),
            isSelected = row.IsSelected, isVisible = row.IsVisible
        };
    }
    private void UpdateNavigationActions()
    {
        if (backNavigationButton is not null) backNavigationButton.IsEnabled = conversationHistory.CanBack;
        if (forwardNavigationButton is not null) forwardNavigationButton.IsEnabled = conversationHistory.CanForward;
        var node = SelectedNavigation();
        if (pinNavigationButton is not null) { pinNavigationButton.IsEnabled = node is not null; pinNavigationButton.Content = node?.Pinned == true ? "Unpin" : "Pin"; }
        if (moreNavigationButton is not null) moreNavigationButton.IsEnabled = node is not null;
        navigationSelection.Text = node is null ? "Select an item to pin, rename or move it." : node.Kind switch
        {
            "project" => "Project · " + node.Title,
            "folder" => "Folder · " + node.Title,
            _ => "Conversation · " + node.Title
        };
        navigationSelection.TextWrapping = TextWrapping.NoWrap; navigationSelection.TextTrimming = TextTrimming.CharacterEllipsis;
        navigationSelection.ToolTip = node?.Path ?? navigationSelection.Text;
        navigationRepository.Text = string.IsNullOrWhiteSpace(projectsDirectory) ? "Project location is configured in Pegasus Cerebral Configuration." : "Projects · " + projectsDirectory;
        navigationRepository.ToolTip = navigationRepository.Text;
    }

    private async Task RefreshNavigationAsync(List<TaskSession> sessions,
        Func<Task<NavigationState>>? readNavigation = null)
    {
        var expectedConnection = ServiceConnectionIdentity;
        var expectedSource = knowledgeConnectionVersion;
        var expectedSelection = conversationSelectionVersion;
        var updated = await (readNavigation?.Invoke() ?? Get<NavigationState>("navigation.get"));
        if (expectedConnection != ServiceConnectionIdentity || expectedSource != knowledgeConnectionVersion ||
            expectedSelection != conversationSelectionVersion) return;
        // A read started before a successful save must not roll back this connection's newer metadata.
        if (updated.Revision < navigation.Revision) return;
        navigationSessions = sessions;
        navigation = updated;
        // Passive updates must preserve the latest explicit project/folder selection. Add and row
        // clicks do not change the active conversation, whose old project may legitimately be null.
        if (selectedNavigationOccurrence?.StartsWith("NavigationSection.", StringComparison.Ordinal) != true &&
            (selectedNavigationId is null || !EffectiveNavigation().Any(x => x.Id == selectedNavigationId)))
            selectedNavigationId = CurrentConversationId is { } current ? NavigationIdForConversation(current) : null;
        RenderNavigation(); UpdateProjectKnowledgeSummary();
        if (conversationHistory.Current is null) RememberCurrentConversationVisit();
    }

    private List<NavigationNode> EffectiveNavigation()
    {
        var all = navigation.Nodes.Where(x => x.Kind != "agent")
            .Where(x => x.Kind != "conversation" || navigationSessions.Any(s => s.Id == x.TargetId)).ToList();
        foreach (var item in navigationSessions)
            if (!all.Any(x => x.Kind == "conversation" && x.TargetId == item.Id))
                all.Add(new NavigationNode { Id = "conversation-" + item.Id, Kind = "conversation", Title = item.Title, TargetId = item.Id, ParentId = all.Any(x => x.Kind == "project" && x.Id == item.ProjectId) ? item.ProjectId : null, Order = all.Count });
        if (CurrentConversationId is { } current && !all.Any(x => x.Kind == "conversation" && x.TargetId == current))
            all.Add(new NavigationNode { Id = "conversation-" + current, Kind = "conversation",
                Title = session?.Title ?? durableConversationTitle, TargetId = current,
                ParentId = all.Any(x => x.Kind == "project" && x.Id == ConversationProjectId) ? ConversationProjectId : null,
                Order = all.Count });
        return all;
    }

    private string NavigationIdForConversation(string id) =>
        EffectiveNavigation().FirstOrDefault(x => x.Kind == "conversation" && x.TargetId == id)?.Id ?? "conversation-" + id;

    private bool IsUnsentNavigationNode(NavigationNode node) => node.Kind == "conversation" &&
        node.TargetId == CurrentConversationId && IsUnsentLocalConversation;

    private DateTimeOffset? NavigationActivity(NavigationNode node) => node.Kind == "conversation"
        ? navigationSessions.FirstOrDefault(x => x.Id == node.TargetId)?.UpdatedAt ?? activeJobs.Where(x => x.SessionId == node.TargetId).Select(x => x.FinishedAt ?? x.CreatedAt).OrderDescending().Select(x => (DateTimeOffset?)x).FirstOrDefault()
        : null;

    private IEnumerable<NavigationNode> OrderNavigation(IEnumerable<NavigationNode> nodes) => nodes
        .OrderBy(x => x.Kind == "conversation" ? 1 : 0)
        .ThenByDescending(NavigationActivity)
        .ThenBy(x => x.Kind == "conversation" ? navigationSessions.FindIndex(s => s.Id == x.TargetId) : x.Order)
        .ThenBy(x => x.Title, StringComparer.CurrentCultureIgnoreCase);

    private void RenderNavigation()
    {
        var raw = EffectiveNavigation();
        static string ChatIdentity(NavigationNode node) => node.TargetId ?? node.Id;
        var canonicalChatIds = raw.Where(node => node.Kind == "conversation")
            .GroupBy(ChatIdentity, StringComparer.Ordinal)
            .Select(group => group.OrderByDescending(node => node.Id == selectedNavigationId)
                .ThenByDescending(node => node.ParentId is { } id &&
                    raw.Any(parent => parent.Id == id && parent.Kind is "folder" or "project"))
                .ThenBy(node => node.Order).ThenBy(node => node.Id, StringComparer.Ordinal).First().Id)
            .ToHashSet(StringComparer.Ordinal);
        // Multiple stored aliases do not create multiple canonical chat rows. Keep metadata intact.
        var all = raw.Where(node => node.Kind != "conversation" || canonicalChatIds.Contains(node.Id)).ToList();
        var query = navigationFilter.Text.Trim();
        bool MatchesRawAlias(NavigationNode node)
        {
            var visited = new HashSet<string>(StringComparer.Ordinal);
            for (NavigationNode? current = node; current is not null && visited.Add(current.Id);
                 current = raw.FirstOrDefault(parent => parent.Id == current.ParentId))
                if (current.Title.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                    (current.Path?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false)) return true;
            return false;
        }
        var matchingAliasChats = query.Length == 0 ? new HashSet<string>(StringComparer.Ordinal) :
            raw.Where(node => node.Kind == "conversation" && MatchesRawAlias(node)).Select(ChatIdentity).ToHashSet(StringComparer.Ordinal);
        var version = ++navigationRenderVersion;
        var scroll = NavigationDescendant<ScrollViewer>(navigationTree);
        var offset = scroll?.VerticalOffset ?? 0;
        if (suppressedNavigationRevealId != selectedNavigationId) suppressedNavigationRevealId = null;
        if (pendingNavigationReveal?.Id != selectedNavigationId) pendingNavigationReveal = null;
        var previousSelected = navigationTree.SelectedItem as TreeViewItem;
        var selectedOccurrence = selectedNavigationOccurrence;
        if (previousSelected?.Tag is NavigationNode previousNode && previousNode.Id == selectedNavigationId)
            selectedOccurrence = AutomationProperties.GetAutomationId(previousSelected);
        var selectedNode = all.FirstOrDefault(node => node.Id == selectedNavigationId);
        NavigationRevealTarget? revealTarget = selectedNode is null ? null :
            new(selectedNode.Id, selectedOccurrence ?? "", selectedNode.Title, selectedNode.ParentId,
                NavigationActivity(selectedNode));
        var sameCompletedOccurrence = revealTarget is { } target && revealedNavigation is { } completed &&
            completed.Id == target.Id && completed.Occurrence == target.Occurrence;
        var pendingSameOccurrence = revealTarget is { } currentTarget && pendingNavigationReveal is { } pendingTarget &&
            pendingTarget.Id == currentTarget.Id && pendingTarget.Occurrence == currentTarget.Occurrence;
        // A metadata reorder keeps a visible selection visible, but does not undo deliberate browsing away.
        var metadataRevealAllowed = !sameCompletedOccurrence || pendingSameOccurrence ||
            previousSelected is not null && NavigationRowVisible(previousSelected, scroll);
        var reveal = revealTarget is { } requestedTarget && revealedNavigation != requestedTarget &&
            metadataRevealAllowed && suppressedNavigationRevealId != selectedNavigationId;
        foreach (var node in all)
            if (knownNavigation.Add(node.Id) && node.Kind is "folder" or "project") expandedNavigation.Add(node.Id);
        if (reveal && selectedNavigationId is not null)
        {
            var current = all.FirstOrDefault(x => x.Id == selectedNavigationId);
            var visited = new HashSet<string>(StringComparer.Ordinal);
            while (current?.ParentId is { } parentId && visited.Add(parentId))
            {
                expandedNavigation.Add(parentId); current = all.FirstOrDefault(x => x.Id == parentId);
            }
        }
        bool Included(NavigationNode node, HashSet<string> visited)
        {
            if (query.Length == 0) return true;
            if (node.Id == selectedNavigationId || node.TargetId == CurrentConversationId ||
                node.Kind == "conversation" && matchingAliasChats.Contains(ChatIdentity(node))) return true;
            for (NavigationNode? current = node; current is not null && visited.Add(current.Id);
                 current = all.FirstOrDefault(parent => parent.Id == current.ParentId))
                if (current.Title.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                    (current.Path?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false)) return true;
            return false;
        }
        var included = all.Where(node => Included(node, [])).Select(node => node.Id).ToHashSet(StringComparer.Ordinal);
        // A matching child keeps its real folder path materialized during search.
        foreach (var match in all.Where(node => included.Contains(node.Id)).ToArray())
        {
            var parent = match.ParentId; var visited = new HashSet<string>(StringComparer.Ordinal);
            while (parent is not null && visited.Add(parent))
            {
                included.Add(parent); parent = all.FirstOrDefault(node => node.Id == parent)?.ParentId;
            }
        }
        bool VisibleNode(NavigationNode node) => query.Length == 0 || included.Contains(node.Id);
        TreeViewItem? selectedItem = null;
        TreeViewItem? selectedSection = null;
        var folderChatIds = new HashSet<string>(StringComparer.Ordinal);
        TreeViewItem Item(NavigationNode node, bool pinnedCopy = false, HashSet<string>? ancestors = null, string? occurrencePrefix = null)
        {
            var chain = ancestors is null ? new HashSet<string>(StringComparer.Ordinal) : new HashSet<string>(ancestors, StringComparer.Ordinal);
            chain.Add(node.Id);
            if (node.Kind == "conversation" && occurrencePrefix != "Recent-") folderChatIds.Add(node.TargetId ?? node.Id);
            var occurrence = (occurrencePrefix ?? (pinnedCopy ? "Pinned-" : "Navigation-")) + node.Id;
            var item = new TreeViewItem { Header = NavigationHeader(node, all), Tag = node,
                ItemContainerStyle = navigationTree.ItemContainerStyle,
                IsExpanded = !pinnedCopy && (query.Length > 0 || expandedNavigation.Contains(node.Id)), ToolTip = node.Path ?? node.Title };
            AutomationProperties.SetAutomationId(item, occurrence);
            AutomationProperties.SetName(item, node.Title + " · " + node.Kind);
            item.SizeChanged += (_, _) => FitNavigationHeader(item);
            item.Loaded += (_, _) => FitNavigationHeader(item);
            item.Expanded += (_, e) => { if (ReferenceEquals(e.OriginalSource, item) && query.Length == 0) expandedNavigation.Add(node.Id); };
            item.Collapsed += (_, e) =>
            {
                if (!ReferenceEquals(e.OriginalSource, item) || query.Length != 0) return;
                expandedNavigation.Remove(node.Id);
                if (!selecting) { suppressedNavigationRevealId = selectedNavigationId; pendingNavigationReveal = null; }
            };
            if (node.Kind == "project") item.MouseDoubleClick += (_, e) =>
            {
                if (!ReferenceEquals(navigationTree.SelectedItem, item)) return;
                e.Handled = true; Run(() => OpenNavigationProject(node));
            };
            if (!IsUnsentNavigationNode(node)) item.ContextMenu = NavigationMenu(node);
            // Pins are shortcuts, so a pinned project does not duplicate its entire conversation tree.
            if (!pinnedCopy)
                foreach (var child in OrderNavigation(all.Where(x => x.ParentId == node.Id && !chain.Contains(x.Id))))
                    if (VisibleNode(child)) item.Items.Add(Item(child, false, chain));
            if (node.Id == selectedNavigationId && (occurrence == selectedOccurrence || selectedItem is null))
                selectedItem = item;
            return item;
        }
        selecting = true;
        try
        {
            navigationTree.Items.Clear();
            var chats = all.Where(node => node.Kind == "conversation").DistinctBy(node => node.TargetId ?? node.Id).ToList();
            var recent = OrderNavigation(chats.Where(VisibleNode)).ToList();
            if (recent.FirstOrDefault(IsUnsentNavigationNode) is { } fresh) { recent.Remove(fresh); recent.Insert(0, fresh); }
            TreeViewItem Section(string key, string title, IEnumerable<NavigationNode> entries, string prefix, bool folder,
                bool canonical = false)
            {
                var header = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 2) };
                if (folder) header.Children.Add(new TextBlock { Text = "\uE8B7", FontFamily = new FontFamily("Segoe MDL2 Assets"),
                    FontSize = 13, Foreground = Ui.Muted, Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center });
                header.Children.Add(new TextBlock { Text = title, FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = Ui.Ink });
                FrameworkElement presentation = key == "Recent" ? header : new Border { BorderBrush = Ui.Line,
                    BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(0, 5, 0, 0), Child = header };
                var group = new TreeViewItem { Header = presentation, IsExpanded = !collapsedNavigationSections.Contains(key),
                    ItemContainerStyle = navigationTree.ItemContainerStyle };
                AutomationProperties.SetAutomationId(group, "NavigationSection." + key);
                AutomationProperties.SetName(group, title);
                if (key == "Projects")
                {
                    var menu = NavigationProjectsMenu();
                    group.ContextMenu = menu;
                    var actions = new Button { Content = "⋯", Width = 24, Height = 20,
                        MinWidth = 0, MinHeight = 0, Padding = new Thickness(3, 0, 3, 0),
                        Margin = new Thickness(7, 0, 0, 0), FontSize = 14, ToolTip = "Create a folder or project" };
                    AutomationProperties.SetAutomationId(actions, "NavigationProjectsActions");
                    AutomationProperties.SetName(actions, "Project and folder actions");
                    actions.Click += (_, e) =>
                    {
                        group.IsSelected = true;
                        menu.PlacementTarget = actions; menu.IsOpen = true; e.Handled = true;
                    };
                    header.Children.Add(actions);
                }
                foreach (var label in header.Children.OfType<TextBlock>())
                    label.SetBinding(TextBlock.ForegroundProperty, new Binding(nameof(Control.Foreground))
                    { RelativeSource = new RelativeSource(RelativeSourceMode.FindAncestor, typeof(TreeViewItem), 1) });
                if (selectedOccurrence == AutomationProperties.GetAutomationId(group)) selectedSection = group;
                group.Expanded += (_, e) =>
                { if (!selecting && ReferenceEquals(e.OriginalSource, group)) collapsedNavigationSections.Remove(key); };
                group.Collapsed += (_, e) =>
                {
                    if (selecting || !ReferenceEquals(e.OriginalSource, group)) return;
                    collapsedNavigationSections.Add(key); suppressedNavigationRevealId = selectedNavigationId; pendingNavigationReveal = null;
                };
                foreach (var node in entries) group.Items.Add(Item(node, pinnedCopy: !canonical, occurrencePrefix: prefix));
                return group;
            }
            navigationTree.Items.Add(Section("Recent", "Recent", recent.Take(sidebarRecentCount), "Recent-", false));
            bool HasRealParent(NavigationNode node) => node.ParentId is { } id && all.Any(parent => parent.Id == id);
            string? ProjectFor(NavigationNode node) => node.TargetId == CurrentConversationId ? ConversationProjectId :
                navigationSessions.FirstOrDefault(item => item.Id == node.TargetId)?.ProjectId;
            var unassigned = chats.Where(node => node.TargetId is not null && ProjectFor(node) is null && !HasRealParent(node)).ToList();
            var projects = Section("Projects", "Projects", OrderNavigation(all.Where(node => !HasRealParent(node) &&
                (node.Kind != "conversation" || !unassigned.Any(chat => chat.Id == node.Id)) && VisibleNode(node))),
                "Navigation-", true, canonical: true);
            navigationTree.Items.Add(projects);
            // This is a presentation group, never a stored NavigationNode or a project assignment.
            var unassignedSection = Section("Unassigned", "Unassigned", OrderNavigation(unassigned.Where(VisibleNode)), "Navigation-", true);
            navigationTree.Items.Add(unassignedSection);
            // Malformed/missing parent paths cannot hide an otherwise available chat.
            foreach (var node in OrderNavigation(chats.Where(node => VisibleNode(node) && !folderChatIds.Contains(node.TargetId ?? node.Id))))
                (node.ParentId is not null || ProjectFor(node) is not null ? projects : unassignedSection)
                    .Items.Add(Item(node, pinnedCopy: true));
            if (selectedItem is not null) { selectedItem.IsSelected = true; selectedNavigationOccurrence = AutomationProperties.GetAutomationId(selectedItem); }
            else if (selectedSection is not null) selectedSection.IsSelected = true;
        }
        finally { selecting = false; }
        if (selectedItem is not null && revealTarget is { } requested)
        {
            revealTarget = requested with { Occurrence = AutomationProperties.GetAutomationId(selectedItem) };
            reveal = revealedNavigation != revealTarget && metadataRevealAllowed &&
                suppressedNavigationRevealId != selectedNavigationId;
            if (selectedItem is not null && !NavigationVirtualGroupOpen(selectedItem)) reveal = false;
            if (reveal) pendingNavigationReveal = revealTarget;
        }
        else if (selectedNavigationId is null) revealedNavigation = null;
        UpdateOlderChatsAvailability(all);
        UpdateNavigationActions();
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, new Action(() =>
        {
            if (version != navigationRenderVersion) return;
            if (selectedNavigationId != revealTarget?.Id ||
                selectedItem is not null && !ReferenceEquals(navigationTree.SelectedItem, selectedItem) ||
                selectedNavigationId is not null && suppressedNavigationRevealId == selectedNavigationId) return;
            var viewer = NavigationDescendant<ScrollViewer>(navigationTree);
            viewer?.ScrollToVerticalOffset(offset);
            if (!reveal || selectedItem is null || revealTarget is not { } pending) return;
            void TryReveal(bool retry)
            {
                if (version != navigationRenderVersion || selectedNavigationId != pending.Id ||
                    !ReferenceEquals(navigationTree.SelectedItem, selectedItem) ||
                    suppressedNavigationRevealId == pending.Id || !NavigationVirtualGroupOpen(selectedItem)) return;
                var currentViewer = NavigationDescendant<ScrollViewer>(navigationTree);
                currentViewer?.UpdateLayout();
                if (!NavigationRowVisible(selectedItem, currentViewer))
                {
                    selectedItem.BringIntoView();
                    currentViewer?.UpdateLayout();
                }
                if (NavigationRowVisible(selectedItem, currentViewer))
                {
                    revealedNavigation = pending;
                    pendingNavigationReveal = null;
                }
                else if (!retry)
                    Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ContextIdle,
                        new Action(() => TryReveal(true)));
            }
            TryReveal(false);
        }));
    }

    private static bool NavigationVirtualGroupOpen(TreeViewItem row)
    {
        for (DependencyObject? item = row; item is not null; item = LogicalTreeHelper.GetParent(item) ??
            (item is Visual ? VisualTreeHelper.GetParent(item) : null))
            if (item is TreeViewItem parent && AutomationProperties.GetAutomationId(parent).StartsWith("NavigationSection.", StringComparison.Ordinal) &&
                !parent.IsExpanded) return false;
        return true;
    }

    private static bool NavigationRowVisible(TreeViewItem row, ScrollViewer? viewer)
    {
        if (viewer is null || row.Visibility != Visibility.Visible || row.ActualHeight <= 0 ||
            viewer.ActualHeight <= 0 || !row.IsDescendantOf(viewer)) return false;
        // IsVisible is false for the deliberately hidden WPF check window, even when layout
        // places a row inside the scroller. Check collapsed ancestors and measured bounds.
        for (DependencyObject? parent = VisualTreeHelper.GetParent(row); parent is not null &&
             !ReferenceEquals(parent, viewer); parent = VisualTreeHelper.GetParent(parent))
            if (parent is TreeViewItem { IsExpanded: false } ||
                parent is UIElement { Visibility: not Visibility.Visible }) return false;
        var bounds = row.TransformToAncestor(viewer).TransformBounds(
            new Rect(new Point(), new Size(row.ActualWidth, row.ActualHeight)));
        return bounds.Right > 0 && bounds.Left < viewer.ActualWidth &&
            Math.Min(bounds.Bottom, viewer.ActualHeight) - Math.Max(bounds.Top, 0) >= Math.Min(bounds.Height, 18);
    }

    private bool IsRecentGeneralChat(NavigationNode node, List<NavigationNode> all)
    {
        if (node.Kind != "conversation" || node.Pinned ||
            navigationSessions.FirstOrDefault(item => item.Id == node.TargetId)?.ProjectId is not null) return false;
        var parent = node.ParentId;
        var visited = new HashSet<string>(StringComparer.Ordinal);
        while (parent is not null && visited.Add(parent))
        {
            var ancestor = all.FirstOrDefault(item => item.Id == parent);
            if (ancestor?.Kind == "project") return false;
            parent = ancestor?.ParentId;
        }
        return true;
    }

    private bool HasHiddenRecentChats(List<NavigationNode> all) => false;

    private void UpdateOlderChatsAvailability(List<NavigationNode> all)
    {
        if (olderChats is not null)
            olderChats.IsEnabled = !showingLegacyNavigation &&
                (HasHiddenRecentChats(all) || durableConversationPage?.NextBeforeRow is not null ||
                    exactWorkflowConversationPage?.NextBeforeRow is not null);
    }

    private async Task ShowOlderChatsAsync()
    {
        if (HasHiddenRecentChats(EffectiveNavigation()))
        {
            showingAllLoadedChats = true;
            RenderNavigation();
            return;
        }
        await RefreshDurableConversationsAsync(true);
    }

    private static void FitNavigationHeader(TreeViewItem item)
    {
        if (item.Header is not FrameworkElement header || item.ActualWidth <= 0 || !header.IsDescendantOf(item)) return;
        // The native tree template measures its header in an Auto column. Constrain that header to
        // this item's allocated width after its actual expander/indent, so wrapping can take effect.
        var left = header.TransformToAncestor(item).Transform(new Point()).X;
        var maximum = Math.Max(0, item.ActualWidth - left - item.Padding.Right - header.Margin.Right);
        if (Math.Abs(header.MaxWidth - maximum) > .5) header.MaxWidth = maximum;
    }

    private FrameworkElement NavigationHeader(NavigationNode node, List<NavigationNode> all)
    {
        static TextBlock FollowSelection(TextBlock text, double opacity = 1)
        {
            text.Opacity = opacity;
            text.SetBinding(TextBlock.ForegroundProperty, new Binding(nameof(Control.Foreground))
            {
                RelativeSource = new RelativeSource(RelativeSourceMode.FindAncestor, typeof(TreeViewItem), 1)
            });
            return text;
        }
        var row = new Grid { Margin = new Thickness(0, 4, 1, 4) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(21) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var glyph = FollowSelection(new TextBlock { FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 13, Margin = new Thickness(0, 2, 4, 0),
            Text = node.Kind switch { "folder" => "\uE8B7", "project" => "\uE821", _ => "\uE8F2" } }, .78);
        row.Children.Add(glyph);
        var label = new StackPanel();
        label.Children.Add(FollowSelection(new TextBlock { Text = node.Kind == "conversation" && !wrapChatNames
                ? string.Join(" ", node.Title.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)) : node.Title,
            TextTrimming = node.Kind == "conversation" && wrapChatNames ? TextTrimming.None : TextTrimming.CharacterEllipsis,
            TextWrapping = node.Kind == "conversation" && wrapChatNames ? TextWrapping.Wrap : TextWrapping.NoWrap,
            ToolTip = node.Kind == "conversation" ? node.Title : node.Path ?? node.Title,
            FontSize = 13, FontWeight = node.Kind is "project" or "folder" ? FontWeights.SemiBold : FontWeights.Normal }));
        string? detail = null;
        if (node.Kind is "project" or "folder")
        {
            var children = all.Where(x => x.ParentId == node.Id).ToList();
            var conversations = children.Count(x => x.Kind == "conversation");
            detail = node.Kind == "project" ? $"{conversations} conversation{(conversations == 1 ? "" : "s")}" :
                $"{children.Count} item{(children.Count == 1 ? "" : "s")}";
        }
        else if (node.Kind == "conversation")
        {
            var job = activeJobs.FirstOrDefault(x => x.SessionId == node.TargetId && Active(x));
            var at = NavigationActivity(node);
            detail = job is null ? (at is { } date ? NavigationDate(date) : null) : job.Status switch
            {
                "waiting-human" => "● Needs your answer",
                "queued" => "● Queued",
                "waiting-helper" => "● Waiting for helper",
                "waiting" or "remote-waiting" or "remote-unconfirmed" => "● Waiting",
                _ => "● Working"
            };
        }
        if (detail is not null) label.Children.Add(FollowSelection(new TextBlock { Text = detail, TextTrimming = TextTrimming.CharacterEllipsis, FontSize = 10.5, Margin = new Thickness(0, 2, 0, 0) }, .76));
        Grid.SetColumn(label, 1); row.Children.Add(label); return row;
    }

    private static string NavigationDate(DateTimeOffset date)
    {
        var local = date.LocalDateTime;
        if (local.Date == DateTime.Today) return "Today · " + local.ToString("t");
        if (local.Date == DateTime.Today.AddDays(-1)) return "Yesterday · " + local.ToString("t");
        return local.ToString("MMM d");
    }

    private ContextMenu NavigationProjectsMenu()
    {
        var menu = new ContextMenu();
        var folder = AddNavigationMenu(menu, "_New folder…", () => AddNavigationNode("folder", atRoot: true));
        AutomationProperties.SetAutomationId(folder, "NavigationProjects.NewFolder");
        var project = AddNavigationMenu(menu, "New _project…", () => AddNavigationNode("project", createDirectory: true, atRoot: true));
        AutomationProperties.SetAutomationId(project, "NavigationProjects.NewProject");
        var existing = AddNavigationMenu(menu, "_Add existing project…", () => AddNavigationNode("project", atRoot: true));
        AutomationProperties.SetAutomationId(existing, "NavigationProjects.AddExistingProject");
        return menu;
    }

    private ContextMenu NavigationMenu(NavigationNode node)
    {
        var menu = new ContextMenu();
        if (node.Kind == "conversation" && node.TargetId is not null)
        {
            AddNavigationMenu(menu, "_Open conversation", () => SelectSession(node.TargetId));
            AddConversationPropertiesMenu(menu, node.TargetId);
        }
        if (node.Kind == "project")
            AddNavigationMenu(menu, "_Open project files", () => OpenNavigationProject(node));
        if (node.Kind is "folder" or "project")
        {
            menu.Items.Add(new Separator());
            AddNavigationMenu(menu, "_New folder…", () => AddNavigationNode("folder", node.Id));
            AddNavigationMenu(menu, "_Create project…", () => AddNavigationNode("project", node.Id, true));
            AddNavigationMenu(menu, "_Add existing project…", () => AddNavigationNode("project", node.Id));
        }
        if (node.Kind == "project")
        {
            AddNavigationMenu(menu, "New con_versation", () => { NewConversationInProject(node.Id); SelectPage("chat"); return Task.CompletedTask; });
        }
        menu.Items.Add(new Separator());
        AddNavigationMenu(menu, node.Pinned ? "_Unpin" : "_Pin", () => ChangeNavigation(node, copy => copy.Pinned = !copy.Pinned));
        AddNavigationMenu(menu, "_Rename…", () => RenameNavigation(node));
        AddNavigationMenu(menu, "_Move to…", () => MoveNavigation(node));
        if (node.Kind == "folder")
        {
            var remove = AddNavigationMenu(menu, "_Remove empty folder…", () => RemoveEmptyNavigationFolderAsync(node));
            AutomationProperties.SetAutomationId(remove, "Navigation.RemoveEmptyFolder");
            remove.IsEnabled = CanRemoveEmptyNavigationFolder(navigation, node);
            remove.ToolTip = "Remove an empty sidebar folder only. No chats, projects or files are deleted.";
        }
        if (node.Kind == "conversation" && node.TargetId is not null)
            AddNavigationMenu(menu, "Change _project…", () => ChangeConversationProjectAsync(node.TargetId));
        menu.Items.Add(new Separator());
        if (node.Kind == "conversation")
        {
            if (node.TargetId is { } chatId && !string.IsNullOrWhiteSpace(chatId))
            {
                // Capture this row's identity; copying never follows selection or grants communication.
                var reference = ChatReference.Create(chatId) ?? chatId;
                AddNavigationMenu(menu, "Copy Cerebral _ID", () => CopyNavigationValue(reference, "Copied Cerebral ID."));
            }
        }
        else AddNavigationMenu(menu, "Copy _name", () => CopyNavigationValue(node.Title, "Copied name."));
        if (!string.IsNullOrWhiteSpace(node.Path))
            AddNavigationMenu(menu, "Copy project _path", () => CopyNavigationValue(node.Path, "Copied project path."));
        AddNavigationMenu(menu, "Re_fresh workspace", RefreshSessions);
        return menu;
    }

    private Task CopyNavigationValue(string value, string status)
    {
        Clipboard.SetText(value); SetStatus(status); return Task.CompletedTask;
    }

    private static T? NavigationDescendant<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T found) return found;
            if (NavigationDescendant<T>(child) is { } nested) return nested;
        }
        return null;
    }

    private static T? NavigationAncestor<T>(DependencyObject? child) where T : DependencyObject
    {
        while (child is not null)
        {
            if (child is T found) return found;
            child = child is Visual ? VisualTreeHelper.GetParent(child) : LogicalTreeHelper.GetParent(child);
        }
        return null;
    }

    private MenuItem AddNavigationMenu(ContextMenu menu, string title, Func<Task> action)
    {
        var item = new MenuItem { Header = title }; item.Click += (_, _) => Run(action); menu.Items.Add(item);
        return item;
    }

    private static bool CanRemoveEmptyNavigationFolder(NavigationState current, NavigationNode expected)
    {
        var matches = current.Nodes.Where(node => node.Id == expected.Id).ToArray();
        return matches.Length == 1 && matches[0] is { Kind: "folder", Path: null, TargetId: null, CreateWhenUsed: false } &&
            System.Text.Json.JsonElement.DeepEquals(CerebralJson.Element(matches[0]), CerebralJson.Element(expected)) &&
            !current.Nodes.Any(node => node.ParentId == expected.Id);
    }

    private static NavigationState PrepareEmptyNavigationFolderRemoval(NavigationState current,
        NavigationNode expected, int expectedRevision)
    {
        if (current.Revision != expectedRevision || !CanRemoveEmptyNavigationFolder(current, expected))
            throw new InvalidOperationException("The folder changed or is not an empty sidebar folder. Refresh before removing it. Nothing was removed.");
        // Preserve every other stored node, including aliases not in the loaded chat viewport.
        // The service compares this unchanged revision before committing the metadata-only edit.
        var edit = CerebralJson.Read<NavigationState>(CerebralJson.Element(current));
        edit.Nodes.RemoveAll(node => node.Id == expected.Id);
        return edit;
    }

    private async Task RemoveEmptyNavigationFolderAsync(NavigationNode selected)
    {
        EnsureConversationControlsSettled();
        var expected = CerebralJson.Read<NavigationNode>(CerebralJson.Element(selected));
        var revision = navigation.Revision;
        var connection = ServiceConnectionIdentity; var source = knowledgeConnectionVersion;
        _ = PrepareEmptyNavigationFolderRemoval(navigation, expected, revision);
        if (MessageBox.Show(this, "Remove the empty sidebar folder “" + expected.Title + "”?\n\n" +
            "Only this empty organizational folder is removed. No chats, projects or files are deleted.",
            "Remove empty folder", MessageBoxButton.OKCancel, MessageBoxImage.Question,
            MessageBoxResult.Cancel) != MessageBoxResult.OK) return;
        EnsureConversationControlsSettled();
        if (connection != ServiceConnectionIdentity || source != knowledgeConnectionVersion)
            throw new InvalidOperationException("The connection changed. Reopen folder actions on the intended workspace. Nothing was sent.");
        var edit = PrepareEmptyNavigationFolderRemoval(navigation, expected, revision);
        var updated = await Get<NavigationState>("navigation.save", new { navigation = edit });
        if (connection != ServiceConnectionIdentity || source != knowledgeConnectionVersion)
        {
            SetStatus("The previous connection confirmed the folder removal. Reconnect there to review it; no repeat was sent.");
            return;
        }
        if (updated.Revision >= navigation.Revision)
        {
            navigation = updated;
            if (selectedNavigationId == expected.Id)
            { selectedNavigationId = null; selectedNavigationOccurrence = "NavigationSection.Projects"; }
            expandedNavigation.Remove(expected.Id); knownNavigation.Remove(expected.Id);
        }
        RenderNavigation();
        SetStatus("Empty sidebar folder removed. No chats, projects or files were deleted.");
    }
    private async Task ChangeNavigation(NavigationNode selected, Action<NavigationNode> change)
    {
        var edit = CerebralJson.Read<NavigationState>(CerebralJson.Element(navigation));
        var node = edit.Nodes.FirstOrDefault(x => x.Id == selected.Id);
        if (node is null) { node = CerebralJson.Read<NavigationNode>(CerebralJson.Element(selected)); edit.Nodes.Add(node); }
        change(node);
        navigation = await Get<NavigationState>("navigation.save", new { navigation = edit });
        RenderNavigation();
    }
    private async Task RenameNavigation(NavigationNode node)
    {
        if (node.Kind == "conversation" && node.TargetId is { } nativeId &&
            (durableConversations.ContainsKey(nativeId) || (!showingLegacyNavigation && !activeJobs.Any(x => x.SessionId == nativeId))))
        { await RenameDurableConversationAsync(nativeId); return; }
        EnsureConversationControlsSettled();
        var form = new FieldsDialog(this, "Rename " + node.Kind, "Choose a name for this item.") { Height = 280 };
        form.Add("title", "Name", node.Title); if (form.ShowDialog() != true) return;
        var title = form.Value("title").Trim(); if (title.Length == 0) throw new ArgumentException("Enter a name.");
        if (node.Kind == "conversation")
        {
            var renamed = await Get<TaskSession>("sessions.rename", new { id = node.TargetId, title });
            if (session?.Id == renamed.Id) { session.Title = renamed.Title; sessionHeading.Text = renamed.Title; }
            await RefreshSessions(); return;
        }
        await ChangeNavigation(node, copy => copy.Title = title);
        await RefreshSessions();
    }
    private async Task MoveNavigation(NavigationNode node)
    {
        var all = EffectiveNavigation(); var excluded = new HashSet<string>(StringComparer.Ordinal) { node.Id };
        bool added;
        do { added = false; foreach (var item in all) if (item.ParentId is not null && excluded.Contains(item.ParentId)) added |= excluded.Add(item.Id); } while (added);
        var targets = all.Where(x => x.Kind is "folder" or "project" && !excluded.Contains(x.Id)).ToList();
        var choices = new Dictionary<string, string?> { ["Workspace root"] = null };
        foreach (var target in targets) choices[target.Title + " · " + target.Id[..Math.Min(8, target.Id.Length)]] = target.Id;
        var selected = choices.FirstOrDefault(x => x.Value == node.ParentId).Key ?? "Workspace root";
        var form = new FieldsDialog(this, "Move " + node.Title, "This changes sidebar organization only. The conversation’s knowledge project stays the same; use Change project to change it. Project files are not moved.") { Height = 300 };
        form.Choice("parent", "Destination", choices.Keys, selected); if (form.ShowDialog() != true) return;
        await ChangeNavigation(node, copy => copy.ParentId = choices[form.Selection("parent")]);
    }
    private async Task AddNavigationNode(string kind, string? parentId = null, bool createDirectory = false, bool atRoot = false)
    {
        var selected = SelectedNavigation();
        if (atRoot) parentId = null;
        else parentId ??= selected?.Kind is "folder" or "project" ? selected.Id : selected?.ParentId;
        var isProject = kind == "project";
        var description = !isProject ? "Folders group projects and conversations in the sidebar; no files are copied or moved." :
            createDirectory ? "Enter one folder name to reserve it under Cerebral Projects. Use Browse only when you deliberately want another allowed location; Cerebral will show the complete resulting path." :
            "Enter the complete existing project path on the worker, or browse for it on this computer. Its files stay where they are; no copy or backup is created.";
        var form = new FieldsDialog(this, isProject ? createDirectory ? "Create project" : "Add existing project" : "Add workspace folder", description,
            createDirectory ? "Create project" : "Add") { Height = isProject ? 460 : 310 };
        form.Add("title", isProject ? "Project name" : "Folder name", "");
        var profile = isProject ? ConversationProfile() : null;
        string? browsedProjectPath = null;
        if (isProject)
        {
            var defaultRoot = DefaultProjectRoot(profile!);
            if (createDirectory)
            {
                form.Add("path", "Project folder name", "");
                form.Add("root", "Default Cerebral projects location", defaultRoot).SetReadOnly("root");
                if (profile!.WorkerId == "local") form.ActionButton("Browse for another parent folder…", dialog =>
                {
                    var picker = new Microsoft.Win32.OpenFolderDialog
                    {
                        Title = "Choose the parent folder for this Cerebral project",
                        InitialDirectory = Directory.Exists(defaultRoot) ? defaultRoot : "",
                        Multiselect = false
                    };
                    if (picker.ShowDialog(this) != true) return;
                    var folder = dialog.Value("path").Trim();
                    if (folder.StartsWith('/') || Path.IsPathFullyQualified(folder)) folder = Path.GetFileName(folder.TrimEnd('/', '\\'));
                    if (folder.Length == 0) folder = dialog.Value("title").Trim();
                    browsedProjectPath = NewProjectPath(picker.FolderName, folder);
                    dialog.SetValue("path", browsedProjectPath);
                });
            }
            else
            {
                form.Add("path", "Complete existing project path", defaultRoot);
                if (profile!.WorkerId == "local") form.ActionButton("Browse this computer…", dialog =>
                {
                    var picker = new Microsoft.Win32.OpenFolderDialog
                    {
                        Title = "Select the existing Cerebral project folder",
                        InitialDirectory = Directory.Exists(dialog.Value("path")) ? dialog.Value("path") :
                            Directory.Exists(defaultRoot) ? defaultRoot : "",
                        Multiselect = false
                    };
                    if (picker.ShowDialog(this) == true) dialog.SetValue("path", picker.FolderName);
                });
            }
        }
        if (form.ShowDialog() != true) return;
        var title = form.Value("title").Trim(); if (title.Length == 0) throw new ArgumentException("Enter a name.");
        string? path = null;
        if (isProject)
        {
            var value = form.Value("path").Trim();
            path = createDirectory
                ? ResolveNewProjectPath(DefaultProjectRoot(profile!), value.Length == 0 ? title : value, browsedProjectPath)
                : value;
            if (string.IsNullOrWhiteSpace(path) || !path.StartsWith('/') && !Path.IsPathFullyQualified(path))
                throw new ArgumentException("Enter the complete absolute project path.");
            if (!profile!.AllowedRoots.Any(root => NavigationPathInside(path, root)))
                throw new ArgumentException("This project location is outside Cerebral's saved folders. Select an allowed location or update its file access in Pegasus Cerebral Configuration.");
            var existingProject = navigation.Nodes.FirstOrDefault(x => x.Kind == "project" && x.Path is not null &&
                NavigationPathInside(path, x.Path) && NavigationPathInside(x.Path, path));
            if (existingProject is not null)
            {
                selectedNavigationId = existingProject.Id; RenderNavigation();
                SetStatus("This folder is already registered as " + existingProject.Title + "."); return;
            }
            if (createDirectory && profile.WorkerId == "local" && (Directory.Exists(path) || File.Exists(path)))
                throw new InvalidOperationException("That project folder already exists. Choose another folder name, or use Add existing project to open it without overwriting anything.");
            // A new project is metadata until work first saves a file. Existing projects are verified now.
            if (!createDirectory)
            {
                var result = await Get<ToolResult>("tools.call", new { profileId = profile.Id, name = "file.list",
                    arguments = new { path, recursive = false, limit = 1 } });
                if (!result.Success) throw new InvalidOperationException(result.Content);
            }
        }
        var node = new NavigationNode { Id = Guid.NewGuid().ToString("N"), Kind = kind, Title = title, ParentId = parentId, Path = path,
            CreateWhenUsed = isProject && createDirectory, Order = navigation.Nodes.Count };
        await ChangeNavigation(node, _ => { });
        selectedNavigationId = node.Id; expandedNavigation.Add(node.Id);
        if (parentId is not null) expandedNavigation.Add(parentId);
        collapsedNavigationSections.Remove("Projects"); RenderNavigation();
        SetStatus(isProject ? createDirectory
            ? "Project added. Its folder will be created at " + path + " when Cerebral first saves work."
            : "Project ready at " + path + ". Select New conversation to work in it."
            : "Workspace folder added.");
    }

    private string DefaultProjectRoot(AgentProfile profile) =>
        profile.WorkerId == "local" && !string.IsNullOrWhiteSpace(projectsDirectory) ? projectsDirectory : profile.AllowedRoots.FirstOrDefault() ?? "";

    private static string NewProjectPath(string parent, string folder)
    {
        if (string.IsNullOrWhiteSpace(parent) || !parent.StartsWith('/') && !Path.IsPathFullyQualified(parent))
            throw new ArgumentException("Choose an absolute parent project location on the worker.");
        if (string.IsNullOrWhiteSpace(folder) || folder is "." or ".." || folder.IndexOfAny(['/', '\\', ':', '*', '?', '"', '<', '>', '|']) >= 0 ||
            folder.Any(char.IsControl) || folder.EndsWith('.') || folder.EndsWith(' '))
            throw new ArgumentException("Use a single folder name without path separators or reserved characters.");
        var stem = folder.Split('.')[0];
        if (new[] { "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
            "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9" }.Contains(stem, StringComparer.OrdinalIgnoreCase))
            throw new ArgumentException("Choose a folder name that is not a reserved device name.");
        var separator = parent.StartsWith('/') && !parent.Contains('\\') ? "/" : "\\";
        var path = parent.TrimEnd('/', '\\') + separator + folder;
        if (!NavigationPathInside(path, parent)) throw new ArgumentException("The new project must stay inside its parent location.");
        return path;
    }

    private static string ResolveNewProjectPath(string defaultRoot, string value, string? selectedByBrowse)
    {
        var wholePath = value.StartsWith('/') || Path.IsPathFullyQualified(value);
        if (!wholePath) return NewProjectPath(defaultRoot, value);
        if (selectedByBrowse is null || !NavigationPathInside(value, selectedByBrowse) ||
            !NavigationPathInside(selectedByBrowse, value))
            throw new ArgumentException("Enter one folder name for Cerebral Projects, or use Browse to choose another complete location.");
        return value;
    }
    private static bool NavigationPathInside(string candidate, string root)
    {
        var windows = root.Contains('\\') || root.Length > 1 && root[1] == ':';
        try
        {
            if (windows) { candidate = Path.GetFullPath(candidate).TrimEnd('\\', '/'); root = Path.GetFullPath(root).TrimEnd('\\', '/'); }
            else { if (!candidate.StartsWith('/') || candidate.Split('/').Any(x => x is "." or "..")) return false; candidate = candidate.TrimEnd('/'); root = root.TrimEnd('/'); }
            return candidate.Equals(root, windows ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal) ||
                candidate.StartsWith(root + (windows ? "\\" : "/"), windows ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
    }
    private async Task OpenNavigationProject(NavigationNode node)
    {
        if (node.Path is null) return;
        sharedFiles.IsChecked = false; filePath.Text = node.Path; SelectPage("files");
        await OpenCurrentFileFolder();
    }
}
