using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using Cerebral.Core;
using Cerebral.Desktop;

namespace Cerebral.Orchestrator;

public sealed partial class OrchestratorWindow
{
    private readonly Dictionary<string, TabItem> pages = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Button> pageButtons = new(StringComparer.Ordinal);
    private Grid? composerSubmit;
    private Grid? composerHeader;
    private Button? composerSendButton;
    private Popup? composerOptionsPopup;
    private Button? composerOptionsButton;
    private Border? connectionBanner;
    private TextBlock? connectionBadge;
    private Button? reconnectServiceButton;
    private Button? pluginSettingsButton;
    private Button? dismissAttentionButton;
    private StackPanel? genericAttentionRow;
    private FrameworkElement? nativeAttentionView;
    private ContentControl? nativeAttentionHost;
    private CompletionOwner? nativeAttentionOwner;
    private object? nativeAttentionSnapshot;
    private int nativeAttentionConnectionVersion;
    private long attentionPublication;
    private BannerNotice? dismissedAttention;
    private BannerNotice? displayedAttention;
    private sealed record BannerNotice(long Publication, object Connection, int Version, string? Identity,
        string? ConversationId, long Selection, string? RunId, long? RunRevision, string? EffectId,
        object? EffectState, string Message, Brush Foreground, CompletionOwner? Owner);
    private string? desktopPluginLoadDiagnostic;
    private GridSplitter? workspaceDivider;
    private GridSplitter? pluginDivider;
    private const double MinimumConversationWidth = 360;

    private void AddPage(string id, string title, UIElement content)
    {
        var page = Ui.TabPage(title, content); page.Tag = id;
        pages.Add(id, page); Tabs.Items.Add(page);
    }

    // Named surfaces are independent of the visible navigation order and plugin-added surfaces.
    private void SelectPage(string id)
    {
        if (id == "activity" && sharedActivityPanel is not null) sharedActivityPanel.IsExpanded = true;
        Tabs.SelectedItem = pages.TryGetValue(id, out var page)
            ? page : throw new ArgumentException("Unknown application surface.", nameof(id));
    }
    private void SetPageContent(string id, UIElement content)
    {
        if (!pages.TryGetValue(id, out var page)) throw new ArgumentException("Unknown application surface.", nameof(id));
        Ui.SetTabPageContent(page, content);
    }

    private FrameworkElement BuildShell(TabControl tabs, TextBlock status)
    {
        var shell = new Grid { Background = Ui.Surface, UseLayoutRounding = true, SnapsToDevicePixels = true };
        shell.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        shell.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        shell.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        shell.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var header = new Grid { Margin = new Thickness(10, 4, 10, 4), MinHeight = 24 };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        AutomationProperties.SetAutomationId(header, "ApplicationHeader");
        var navigation = new StackPanel { Orientation = Orientation.Horizontal };
        workspaceVisibilityButton = ShellButton("☰", "ToggleWorkspaceSidebar", "Show or hide projects and conversations", () => ToggleCompactRail("workspace"));
        navigation.Children.Add(workspaceVisibilityButton);
        // Workflows remain a callable internal surface for active forms, diagnostics and recovery.
        // Their normal progress is rendered inline in Chat, so a second user-facing Work tab would
        // duplicate the conversation and expose service plumbing as primary navigation.
        foreach (var (id, title) in new[] { ("chat", "Chat"), ("files", "Files"), ("activity", "Activity") })
        {
            var button = ShellButton(title, "ApplicationPage." + id, "Open " + title, () => SelectPage(id));
            pageButtons.Add(id, button); navigation.Children.Add(button);
        }
        navigation.Children.Add(ShellButton("New chat", "NewConversation", "Start a new chat", NewConversation));
        header.Children.Add(navigation);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        connectionBadge = Ui.Text("Offline", 11, Ui.Muted); connectionBadge.Margin = new Thickness(8, 0, 10, 0);
        connectionBadge.VerticalAlignment = VerticalAlignment.Center;
        AutomationProperties.SetAutomationId(connectionBadge, "ConnectionOwnership");
        actions.Children.Add(connectionBadge);
        activityVisibilityButton = ShellButton("Panels", "ToggleActivitySidebar", "Show or hide plugin panels", () => ToggleCompactRail("activity"));
        actions.Children.Add(activityVisibilityButton);
        actions.Children.Add(ShellButton("Settings", "OpenConfiguration", "Open Pegasus Cerebral Configuration", () =>
            Run(OpenConnectionConfigurationAsync)));
        Button? more = null;
        more = ShellButton("⋯", "ApplicationMore", "Application tools and connection details", () =>
        {
            var menu = new ContextMenu();
            menu.Items.Add(BuildChatActionsMenu());
            AddMenuAction(menu, "ExitCerebral", "Exit Cerebral", () => { RequestFullExit(); return Task.CompletedTask; });
            AddMenuAction(menu, "OpenApplicationTools", "Tools", () => { SelectPage("tools"); return Task.CompletedTask; });
            AddMenuAction(menu, "OpenWorkflowDetails", "Workflow details", () => { SelectPage("work"); return Task.CompletedTask; });
            AddAppearanceMenu(menu);
            AddMenuAction(menu, "ApplicationReconnect", "Reconnect", ConnectAsync);
            AddMenuAction(menu, "ApplicationConnectionDetails", "Connection details", () => { Ui.ShowText(this, "Connection", ServiceStatusText); return Task.CompletedTask; });
            AddMenuAction(menu, "ApplicationAbout", "About Pegasus Cerebral", () => { Ui.ShowAbout(this, "orchestrator", typeof(OrchestratorWindow).Assembly); return Task.CompletedTask; });
            var showStatus = new MenuItem { Header = "Show Cerebral status", IsCheckable = true, IsChecked = SystemStatusVisible };
            showStatus.Click += (_, _) => SetSystemStatusVisibility(showStatus.IsChecked);
            menu.Items.Add(showStatus);
            menu.PlacementTarget = more; menu.IsOpen = true;
        });
        actions.Children.Add(more); Grid.SetColumn(actions, 1); header.Children.Add(actions);
        shell.Children.Add(new Border { Child = header, Background = Ui.Paper, BorderBrush = Ui.Line, BorderThickness = new Thickness(0, 0, 0, 1) });

        status.Margin = new Thickness(0); status.TextWrapping = TextWrapping.Wrap; status.TextTrimming = TextTrimming.None;
        status.VerticalAlignment = VerticalAlignment.Center; status.SetBinding(ToolTipProperty, new Binding(nameof(TextBlock.Text)) { Source = status });
        reconnectServiceButton = ShellButton("Reconnect", "ReconnectService", "Reconnect to Cerebral", () => Run(ConnectAsync));
        pluginSettingsButton = ShellButton("Open Configuration", "ReviewDesktopPlugins", "Open Configuration", () =>
            Run(OpenConnectionConfigurationAsync));
        pluginSettingsButton.ToolTip = "Open Configuration for this Worker, then select Plugins & tools > Desktop plugins to review plugins.";
        AutomationProperties.SetHelpText(pluginSettingsButton, (string)pluginSettingsButton.ToolTip);
        dismissAttentionButton = ShellButton("Dismiss", "DismissCurrentAttention", "Dismiss this notice without changing the request outcome", () =>
        {
            if (IsConnected && Equals(Status.Foreground, Ui.ErrorText) && displayedAttention is { } expected &&
                SameBannerNotice(expected, CaptureBannerNotice())) dismissedAttention = expected;
            RefreshShellConnection();
        });
        var bannerActions = Ui.Bar(reconnectServiceButton, pluginSettingsButton);
        bannerActions.Margin = new Thickness(0);
        var generic = new DockPanel();
        DockPanel.SetDock(bannerActions, Dock.Right); generic.Children.Add(bannerActions);
        var statusText = new ScrollViewer { Content = status, MaxHeight = 72,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        AutomationProperties.SetAutomationId(statusText, "ConnectionAttentionText");
        AutomationProperties.SetName(statusText, "Complete connection or desktop diagnostic");
        generic.Children.Add(statusText);
        genericAttentionRow = new StackPanel(); genericAttentionRow.Children.Add(generic);
        nativeAttentionHost = new ContentControl { HorizontalContentAlignment = HorizontalAlignment.Stretch };
        if (nativeChat is not null)
        {
            nativeAttentionView = nativeChat.CreateHostAttentionView(NativeAttentionCurrent);
            nativeAttentionHost.Content = nativeAttentionView;
            nativeChat.SnapshotChanged += (_, _) => { CaptureNativeAttentionOwner(); RefreshShellConnection(); };
            CaptureNativeAttentionOwner();
        }
        var bannerRows = new StackPanel(); bannerRows.Children.Add(genericAttentionRow); bannerRows.Children.Add(nativeAttentionHost);
        var bannerContent = new DockPanel();
        DockPanel.SetDock(dismissAttentionButton, Dock.Right); bannerContent.Children.Add(dismissAttentionButton);
        bannerContent.Children.Add(bannerRows);
        connectionBanner = new Border { Child = bannerContent, Padding = new Thickness(14, 3, 10, 3), Background = Ui.Paper,
            BorderBrush = Ui.Line, BorderThickness = new Thickness(0, 0, 0, 1) };
        AutomationProperties.SetAutomationId(connectionBanner, "ConnectionBanner");
        Grid.SetRow(connectionBanner, 1); shell.Children.Add(connectionBanner);
        // TabControl still owns content selection, keyboard focus and existing automation identity.
        // Its presentation only shows the selected surface; the compact app row owns navigation.
        // TabItem automation reaches the selected page through this standard WPF part.
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter), "PART_SelectedContentHost");
        presenter.SetValue(ContentPresenter.ContentSourceProperty, "SelectedContent");
        // The standard tab peers require realized TabItem containers as well as the content host.
        // Keep their item host in layout without drawing duplicate navigation headers.
        var tabRoot = new FrameworkElementFactory(typeof(Grid));
        var headers = new FrameworkElementFactory(typeof(ItemsPresenter));
        headers.SetValue(FrameworkElement.WidthProperty, 0d);
        headers.SetValue(FrameworkElement.HeightProperty, 0d);
        headers.SetValue(UIElement.VisibilityProperty, Visibility.Hidden);
        headers.SetValue(UIElement.IsHitTestVisibleProperty, false);
        tabRoot.AppendChild(headers);
        tabRoot.AppendChild(presenter);
        tabs.Template = new ControlTemplate(typeof(TabControl)) { VisualTree = tabRoot };
        tabs.Margin = new Thickness(0); tabs.Padding = new Thickness(0); tabs.BorderThickness = new Thickness(0);
        tabs.HorizontalContentAlignment = HorizontalAlignment.Stretch; tabs.VerticalContentAlignment = VerticalAlignment.Stretch;
        Grid.SetRow(tabs, 2); shell.Children.Add(tabs);
        var systemStatus = BuildSystemStatusBar(); Grid.SetRow(systemStatus, 3); shell.Children.Add(systemStatus);
        tabs.SelectionChanged += (_, args) =>
        {
            if (!ReferenceEquals(args.OriginalSource, tabs)) return;
            RefreshPageButtons();
            if (tabs.SelectedItem is TabItem { Tag: "activity" }) QueueSharedActivityUpdate();
            if (IsLoaded && IsVisible && tabs.SelectedItem is TabItem { Tag: string id } && id != "work")
                SavePortableInterfaceString(InterfacePreferencePolicy.SelectedSurface, "Cerebral.Surface", id);
        };
        tabs.Loaded += (_, _) =>
        {
            if (UiPreferenceStore.ReadString("Cerebral.Surface") is { } id && id != "work" && (id == "activity" || pages.ContainsKey(id))) SelectPage(id);
            else SelectPage("chat");
            RefreshPageButtons();
        };
        ServiceStatusChanged += (_, _) =>
        {
            void PublishAttention() { attentionPublication++; dismissedAttention = null; RefreshShellConnection(); }
            if (Dispatcher.CheckAccess()) PublishAttention(); else Dispatcher.BeginInvoke(new Action(PublishAttention));
        };
        // A dismissed notice belongs to its exact owner, chat, effect and publication. Selection alone must not hide another chat's error.
        shell.LayoutUpdated += (_, _) =>
        {
            if (dismissedAttention is { } hidden && !SameBannerNotice(hidden, CaptureBannerNotice()))
            { dismissedAttention = null; RefreshShellConnection(); }
        };
        SelectPage("chat"); RefreshPageButtons(); RefreshShellConnection();
        return shell;
    }

    private static Button ShellButton(string label, string id, string name, Action action)
    {
        var button = Ui.Button(label, action); button.MinHeight = 24; button.Padding = new Thickness(10, 2, 10, 2);
        button.VerticalAlignment = VerticalAlignment.Center;
        button.Margin = new Thickness(0, 0, 4, 0); button.FontSize = 13; button.ToolTip = name;
        AutomationProperties.SetAutomationId(button, id); AutomationProperties.SetName(button, name); return button;
    }
    private void RefreshPageButtons()
    {
        foreach (var (id, button) in pageButtons)
        {
            var selected = pages.TryGetValue(id, out var page) && ReferenceEquals(Tabs.SelectedItem, page);
            button.Background = selected ? Ui.Accent : Brushes.Transparent; button.Foreground = selected ? Ui.OnAccent : Ui.Ink;
            button.BorderBrush = selected ? Ui.Accent : Brushes.Transparent;
        }
    }
    private void RefreshShellConnection()
    {
        if (connectionBadge is null || connectionBanner is null) return;
        var error = Equals(Status.Foreground, Ui.ErrorText);
        connectionBadge.Text = error ? "Attention" : IsConnected ? "Connected" : "Offline";
        connectionBadge.Foreground = error ? Ui.ErrorText : Ui.Muted;
        connectionBadge.ToolTip = ServiceStatusText;
        connectionBanner.Background = error ? Ui.ErrorBackground : Ui.Paper;
        connectionBanner.BorderBrush = error ? Ui.ErrorBorder : Ui.Line;
        var nativeVisible = NativeAttentionCurrent() && nativeAttentionView?.Visibility == Visibility.Visible;
        if (nativeAttentionHost is not null) nativeAttentionHost.Visibility = nativeVisible ? Visibility.Visible : Visibility.Collapsed;
        if (nativeAttentionView is not null) nativeAttentionView.IsEnabled = NativeAttentionCurrent();
        if (genericAttentionRow is not null)
            genericAttentionRow.Visibility = !IsConnected || error && !(nativeVisible && CompletionMirrorCurrent()) ? Visibility.Visible : Visibility.Collapsed;
        if (dismissAttentionButton is not null) dismissAttentionButton.Visibility = IsConnected && error ? Visibility.Visible : Visibility.Collapsed;
        displayedAttention = CaptureBannerNotice();
        var hidden = IsConnected && dismissedAttention is { } notice && SameBannerNotice(notice, displayedAttention);
        connectionBanner.Visibility = !hidden && (!IsConnected || error || nativeVisible) ? Visibility.Visible : Visibility.Collapsed;
        // A connected desktop error is not a broken worker connection. Only offer plugin
        // settings while the displayed diagnostic still belongs to plugin loading.
        RefreshConnectionBannerActions(IsConnected, error && desktopPluginLoadDiagnostic is not null &&
            string.Equals(ServiceStatusText, desktopPluginLoadDiagnostic, StringComparison.Ordinal));
    }

    private void CaptureNativeAttentionOwner()
    {
        nativeAttentionOwner = mainCompletionOwner; nativeAttentionSnapshot = nativeChat?.Snapshot;
        nativeAttentionConnectionVersion = knowledgeConnectionVersion;
    }

    private bool NativeAttentionCurrent() => IsConnected && IsNativeChat &&
        nativeChat?.Snapshot is { } snapshot && snapshot.ConversationId == CurrentConversationId &&
        ReferenceEquals(snapshot, nativeAttentionSnapshot) && nativeAttentionConnectionVersion == knowledgeConnectionVersion &&
        nativeAttentionOwner is { } owner && CompletionOwnerCurrent(owner) && RetainedNativeOwnerCurrent();

    private BannerNotice CaptureBannerNotice()
    {
        var run = nativeChat?.Snapshot?.Run;
        return new(attentionPublication, ServiceConnection, knowledgeConnectionVersion, ServiceConnectionIdentity,
            CurrentConversationId, conversationSelectionVersion, run?.Id, run?.Revision, run?.EffectId, run?.EffectState,
            Status.Text, Status.Foreground, mainCompletionOwner);
    }

    private static bool SameBannerNotice(BannerNotice left, BannerNotice right) =>
        ReferenceEquals(left.Connection, right.Connection) && ReferenceEquals(left.Owner, right.Owner) &&
        ReferenceEquals(left.Foreground, right.Foreground) && left == right;

    private void RefreshConnectionBannerActions(bool connected, bool pluginLoadError)
    {
        if (reconnectServiceButton is not null)
            reconnectServiceButton.Visibility = connected ? Visibility.Collapsed : Visibility.Visible;
        if (pluginSettingsButton is not null)
            pluginSettingsButton.Visibility = connected && pluginLoadError ? Visibility.Visible : Visibility.Collapsed;
    }

    private UIElement ConversationPage()
    {
        var grid = new Grid { Margin = new Thickness(10, 0, 10, 2), ClipToBounds = true }; conversationLayout = grid;
        workspaceColumn = new ColumnDefinition { Width = new GridLength(workspacePreferredWidth) };
        workspaceSplitterColumn = new ColumnDefinition { Width = new GridLength(7) };
        activitySplitterColumn = new ColumnDefinition { Width = new GridLength(7) };
        activityColumn = new ColumnDefinition { Width = new GridLength(0) };
        grid.ColumnDefinitions.Add(workspaceColumn); grid.ColumnDefinitions.Add(workspaceSplitterColumn);
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = MinimumConversationWidth });
        grid.ColumnDefinitions.Add(activitySplitterColumn); grid.ColumnDefinitions.Add(activityColumn);
        Resources[DesktopBrandWatermark.VisibilityResourceKey] = UiPreferenceStore.ReadBool("Cerebral.ChatWatermarkVisible", true)
            ? Visibility.Visible : Visibility.Collapsed;
        var watermark = DesktopBrandWatermark.Create();
        Grid.SetColumnSpan(watermark, grid.ColumnDefinitions.Count); grid.Children.Add(watermark);
        workspaceRail = (FrameworkElement)NavigationView(); workspaceRail.ClipToBounds = true; grid.Children.Add(workspaceRail);
        workspaceDivider = Divider("WorkspaceSidebarDivider", "Resize workspace sidebar");
        workspaceDivider.DragCompleted += (_, args) => { if (!args.Canceled) RememberWorkspaceWidth(); };
        workspaceDivider.KeyUp += (_, args) =>
        {
            if (args.Key is System.Windows.Input.Key.Left or System.Windows.Input.Key.Right) RememberWorkspaceWidth();
        };
        workspaceDivider.Cursor = System.Windows.Input.Cursors.SizeWE;
        Grid.SetColumn(workspaceDivider, 1); grid.Children.Add(workspaceDivider);
        var center = new Grid { ClipToBounds = true }; conversationCenter = center;
        center.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        center.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        center.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        center.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        center.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        SetBinding(TitleProperty, new Binding(nameof(TextBlock.Text)) { Source = sessionHeading, StringFormat = "Pegasus Cerebral — {0}" });
        sessionHeading.FontSize = 16; sessionHeading.FontWeight = FontWeights.SemiBold;
        sessionHeading.TextWrapping = TextWrapping.NoWrap; sessionHeading.TextTrimming = TextTrimming.CharacterEllipsis;
        sessionHeading.Margin = new Thickness(0, 0, 12, 0); sessionHeading.VerticalAlignment = VerticalAlignment.Center;
        sessionHeading.SetBinding(ToolTipProperty, new Binding(nameof(TextBlock.Text)) { Source = sessionHeading });
        sessionState.TextWrapping = TextWrapping.NoWrap; sessionState.TextTrimming = TextTrimming.CharacterEllipsis;
        sessionState.Margin = new Thickness(0); sessionState.VerticalAlignment = VerticalAlignment.Center;
        sessionState.SetBinding(ToolTipProperty, new Binding(nameof(TextBlock.Text)) { Source = sessionState });
        agentSummary.Visibility = Visibility.Collapsed;
        var header = new Grid { Height = 44 };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 40 });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.Children.Add(sessionState);
        projectKnowledgeControls = (FrameworkElement)ProjectKnowledgeView(); projectKnowledgeControls.MaxWidth = 180;
        projectKnowledgeControls.Margin = new Thickness(12, 0, 8, 0); projectKnowledgeControls.VerticalAlignment = VerticalAlignment.Center;
        projectKnowledgeControls.SetBinding(ToolTipProperty, new Binding(nameof(TextBlock.Text)) { Source = projectKnowledgeSummary });
        Grid.SetColumn(projectKnowledgeControls, 1); header.Children.Add(projectKnowledgeControls);
        compactActionsButton = ShellButton("⋯", "ConversationActions", "Conversation actions", OpenCompactConversationActions);
        Grid.SetColumn(compactActionsButton, 2); header.Children.Add(compactActionsButton);
        conversationHeaderStrip = new Border { Child = header, BorderBrush = Ui.Line, BorderThickness = new Thickness(0, 0, 0, 1), Margin = new Thickness(0, 0, 0, 8) };
        AutomationProperties.SetAutomationId(conversationHeaderStrip, "ConversationHeaderStrip");
        AutomationProperties.SetName(conversationHeaderStrip, "Main conversation status and actions");
        center.Children.Add(conversationHeaderStrip); Grid.SetRow(interfaceRequests, 1); center.Children.Add(interfaceRequests);
        InitializeConversationWorkspaceSurface(center);

        var composerLayer = new Grid { HorizontalAlignment = HorizontalAlignment.Stretch }; composerLayerHost = composerLayer;
        composerLayer.Children.Add(desktopInputGuidanceHost);
        var composer = new StackPanel { Margin = new Thickness(0, 4, 0, 0) }; conversationComposer = composer;
        prompt.Padding = new Thickness(10, 4, 10, 4);
        liveLabel.Margin = new Thickness(0, 0, 0, 3); composer.Children.Add(liveLabel); composer.Children.Add(liveDetails);
        composerCaption = Ui.Text("Message Cerebral", 12, Ui.Muted); composerCaption.Margin = new Thickness(0, 0, 8, 0);
        composerCaption.VerticalAlignment = VerticalAlignment.Center; composerCaption.Visibility = Visibility.Collapsed;
        composerCaption.TextTrimming = TextTrimming.CharacterEllipsis;
        // The existing responsive options host now shares the action footer below the editor.
        composerHeader = new Grid { Margin = new Thickness(0, 4, 0, 0) };
        composerHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        composerHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        composerHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        AutomationProperties.SetAutomationId(composerHeader, "ComposerHeader");
        AutomationProperties.SetName(composerHeader, "Message actions and options");
        composer.Children.Add(CreateComposerSuggestionSurface()); InitializeAttachmentComposer(composer);
        var send = ShellButton("Send", "SendMessage", "Send message", () => Run(Send));
        composerSendButton = send;
        send.Background = Ui.Accent; send.Foreground = Ui.OnAccent;
        // A mouse press normally moves keyboard focus to a WPF Button before Click runs. Keep the
        // composer focused through this one transition so input plugins can finish their pending
        // text in the general pre-submit hook. Restore normal keyboard navigation on release.
        send.PreviewMouseUp += (_, _) => send.Focusable = true;
        send.LostMouseCapture += (_, _) => send.Focusable = true;
        stopButton = ShellButton("Stop", "StopConversation", "Stop this conversation and its helper work", RequestConversationStop);
        var tools = BuildComposerPluginTools();
        var submission = Ui.Bar(send, stopButton,
            ShellButton("Steer", "SteerConversation", "Steer active work", () => Run(Steer)),
            ShellButton("Attach", "AttachFiles", "Attach files or images", PickAttachments), tools);
        foreach (var control in submission.Children.OfType<Control>()) CompactComposerControl(control);
        AutomationProperties.SetAutomationId(submission, "ComposerPrimaryActions");
        AutomationProperties.SetName(submission, "Send and conversation actions");
        submission.Margin = new Thickness(0); submission.VerticalAlignment = VerticalAlignment.Top;
        composerSubmit = composerHeader;
        composerSubmit.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        composerSubmit.Children.Add(submission);
        composerOptionsPanel = new WrapPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        enterSends.MinHeight = 24; enterSends.Margin = new Thickness(10, 0, 6, 0);
        composerOptionsPanel.Children.Add(conversationTokenBalanceExpander); composerOptionsPanel.Children.Add(enterSends);
        Grid.SetColumn(composerOptionsPanel, 1); composerHeader.Children.Add(composerOptionsPanel);
        composerOptionsButton = ShellButton("Message options", "ComposerInputOptions", "Response balance and Enter behavior", () =>
        {
            if (composerOptionsPopup is null) return;
            if (composerToolsPopup is not null) composerToolsPopup.IsOpen = false;
            if (composerDictationPopup is not null) composerDictationPopup.IsOpen = false;
            composerOptionsPopup.IsOpen = !composerOptionsPopup.IsOpen;
        });
        CompactComposerControl(composerOptionsButton); CompactComposerControl(composerDictationButton!);
        composerOptionsButton.Visibility = Visibility.Collapsed; Grid.SetColumn(composerOptionsButton, 1); composerHeader.Children.Add(composerOptionsButton);
        Grid.SetColumn(composerDictationButton!, 2); composerHeader.Children.Add(composerDictationButton!);
        composerOptionsPopup = new Popup { PlacementTarget = composerOptionsButton, Placement = PlacementMode.Top, StaysOpen = false, AllowsTransparency = true };
        Closed += (_, _) => composerOptionsPopup.IsOpen = false;
        composer.Children.Add(composerSubmit); composerLayer.Children.Add(composer);
        var questionLayout = new StackPanel();
        var questionHeading = Ui.Text("Needs your input", 22); questionHeading.FontWeight = FontWeights.SemiBold;
        questionLayout.Children.Add(questionHeading); questionLayout.Children.Add(questionPanel);
        questionHost = new Border { Child = questionLayout, Visibility = Visibility.Collapsed, Padding = new Thickness(0, 16, 0, 0),
            Margin = new Thickness(0, 12, 0, 0), BorderBrush = Ui.Accent, BorderThickness = new Thickness(0, 2, 0, 0) };
        AutomationProperties.SetAutomationId(questionHost, "ConversationQuestionHost");
        AutomationProperties.SetName(questionHost, "Questions requiring input in this conversation");
        questionHost.IsVisibleChanged += (_, _) => UpdateComposerQuestionSurface();
        transcript.Content = null;
        var conversationFlow = new StackPanel(); conversationFlow.Children.Add(messages); conversationFlow.Children.Add(questionHost);
        transcript.Content = conversationFlow;
        composerVerticalDivider = CreateComposerVerticalDivider();
        Grid.SetRow(composerVerticalDivider, 3); center.Children.Add(composerVerticalDivider);
        Grid.SetRow(composerLayer, 4); center.Children.Add(composerLayer);
        composerLayer.SizeChanged += (_, _) => UpdateDesktopInputGuidanceLayout();
        // A narrower row can wrap buttons without changing its allocated height. Reconcile the
        // remaining text height after those controls measure, preserving the user's divider size.
        composer.LayoutUpdated += (_, _) => { if (composerPreferredHeight is not null) UpdateDesktopInputGuidanceLayout(); };
        var workPanels = CreateConversationPanelStack(center);
        Grid.SetColumn(workPanels, 2); grid.Children.Add(workPanels);
        pluginDivider = Divider("PluginSidebarDivider", "Resize plugin sidebar");
        void RememberActivityWidth()
        {
            if (activityColumn.ActualWidth >= 280) activityPreferredWidth = Math.Clamp(activityColumn.ActualWidth, 280, 520);
            UpdateConversationLayout(); MainLayoutChanged();
        }
        pluginDivider.DragCompleted += (_, args) => { if (!args.Canceled) RememberActivityWidth(); };
        pluginDivider.KeyUp += (_, args) =>
        {
            if (args.Key is System.Windows.Input.Key.Left or System.Windows.Input.Key.Right) RememberActivityWidth();
        };
        Grid.SetColumn(pluginDivider, 3); grid.Children.Add(pluginDivider);
        activityRail = new Border { Child = BuildConversationRightRail(), ClipToBounds = true, Padding = new Thickness(8, 6, 0, 0) };
        AutomationProperties.SetAutomationId(activityRail, "DesktopSidebarHost"); AutomationProperties.SetName(activityRail, "Plugin panels");
        Grid.SetColumn(activityRail, 4); grid.Children.Add(activityRail);
        var panelItems = (System.Collections.Specialized.INotifyCollectionChanged)desktopSidebarTabs.Items;
        System.Collections.Specialized.NotifyCollectionChangedEventHandler panelsChanged = (_, _) => UpdateConversationLayout();
        panelItems.CollectionChanged += panelsChanged;
        Closed += (_, _) => panelItems.CollectionChanged -= panelsChanged;
        grid.SizeChanged += (_, _) => UpdateConversationLayout(); grid.Loaded += (_, _) => UpdateConversationLayout();
        return grid;
    }

    private static void CompactComposerControl(Control control)
    {
        // Local composer chrome stays compact without clipping larger text or shrinking plugin mounts.
        control.MinHeight = 24; control.Height = double.NaN;
        control.Padding = new Thickness(Math.Max(6, control.Padding.Left), 2, Math.Max(6, control.Padding.Right), 2);
        control.Margin = new Thickness(control.Margin.Left, 0, control.Margin.Right, 0);
    }

    private static GridSplitter Divider(string id, string name)
    {
        var divider = new GridSplitter { Width = 5, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Stretch,
            ResizeDirection = GridResizeDirection.Columns, ResizeBehavior = GridResizeBehavior.PreviousAndNext, Background = Ui.Line };
        AutomationProperties.SetAutomationId(divider, id); AutomationProperties.SetName(divider, name); return divider;
    }
    private void RememberWorkspaceWidth()
    {
        if (workspaceColumn is not null && double.IsFinite(workspaceColumn.ActualWidth) && workspaceColumn.ActualWidth >= 208)
            workspacePreferredWidth = workspaceColumn.ActualWidth;
        UpdateConversationLayout();
        MainLayoutChanged();
    }

    private void UpdateComposerQuestionSurface() => UpdateDesktopInputGuidanceLayout();
    private void ToggleCompactRail(string rail)
    {
        if (rail == "workspace")
        {
            workspaceRequested = workspaceRail?.Visibility != Visibility.Visible;
            compactWorkspaceOpen = workspaceRequested;
            if (workspaceRequested && (conversationLayout?.ActualWidth ?? 0) < ActivitySidebarAutomaticWidth) SetConversationDockVisibility(false);
        }
        else
        {
            if (!desktopSidebarTabs.HasItems) { UpdateConversationLayout(); return; }
            SetConversationDockVisibility(!activityRequested);
        }
        if (IsLoaded && IsVisible)
        {
            SavePortableInterfaceBool(InterfacePreferencePolicy.NavigationVisible, "Cerebral.NavigationVisible", workspaceRequested);
            SavePortableInterfaceBool(InterfacePreferencePolicy.PanelsVisible, "Cerebral.PanelsVisible", activityRequested);
        }
        UpdateConversationLayout();
    }
    private void OpenCompactConversationActions()
    {
        if (compactActionsButton is null) return;
        var menu = ConversationActionsMenu(); menu.PlacementTarget = compactActionsButton; menu.IsOpen = true;
    }
    private void AddMenuAction(ContextMenu menu, string id, string title, Func<Task> action, string? help = null)
    {
        var item = new MenuItem { Header = title }; AutomationProperties.SetAutomationId(item, id); AutomationProperties.SetName(item, title);
        if (help is not null) { item.ToolTip = help; AutomationProperties.SetHelpText(item, help); }
        item.Click += (_, _) => Run(action); menu.Items.Add(item);
    }

    private void AddAppearanceMenu(ContextMenu menu)
    {
        var parent = new MenuItem { Header = "Appearance" };
        AutomationProperties.SetAutomationId(parent, "ApplicationAppearance");
        foreach (var (id, title) in new[]
                 {
                     (AppearanceThemePolicy.System, "System"),
                     (AppearanceThemePolicy.Light, "Light"),
                     (AppearanceThemePolicy.Dark, "Dark")
                 })
        {
            var item = new MenuItem { Header = title, IsCheckable = true, IsChecked = Ui.AppearanceTheme == id };
            AutomationProperties.SetAutomationId(item, "ApplicationAppearance." + id);
            item.Click += (_, _) => SelectAppearanceTheme(id);
            parent.Items.Add(item);
        }
        parent.Items.Add(new Separator());
        var watermark = new MenuItem
        {
            Header = "Embossed watermark", IsCheckable = true,
            IsChecked = TryFindResource(DesktopBrandWatermark.VisibilityResourceKey) is not Visibility value || value == Visibility.Visible
        };
        AutomationProperties.SetAutomationId(watermark, "ApplicationAppearance.Watermark");
        watermark.Click += (_, _) =>
        {
            try
            {
                if (IsLoaded && IsVisible) UiPreferenceStore.WriteBool("Cerebral.ChatWatermarkVisible", watermark.IsChecked);
                Resources[DesktopBrandWatermark.VisibilityResourceKey] = watermark.IsChecked ? Visibility.Visible : Visibility.Collapsed;
            }
            catch (Exception error) { SetStatus("Watermark preference could not be saved: " + error.Message, true); }
        };
        parent.Items.Add(watermark);
        AddSidebarAppearanceItems(parent);
        menu.Items.Add(parent);
    }
    private ContextMenu ConversationActionsMenu()
    {
        var menu = new ContextMenu();
        if (ControlUncertain)
            AddMenuAction(menu, "RetryConversationAction", "Retry pending action", RetryConversationControlAsync);
        AddMenuAction(menu, "ConversationDetails", "Conversation details", () => { Ui.ShowText(this, "Conversation details", sessionHeading.Text + "\n\n" + sessionState.Text + "\n\n" + agentSummary.Text + "\n" + projectKnowledgeSummary.Text); return Task.CompletedTask; });
        AddConversationPropertiesMenu(menu, CurrentConversationId);
        AddMenuAction(menu, "ToggleWorkspaceSidebarMenuItem", workspaceRail?.Visibility == Visibility.Visible ? "Hide navigation" : "Show navigation", () => { ToggleCompactRail("workspace"); return Task.CompletedTask; });
        AddMenuAction(menu, "ToggleActivitySidebarMenuItem", activityRail?.Visibility == Visibility.Visible ? "Hide panels" : "Show panels", () => { ToggleCompactRail("activity"); return Task.CompletedTask; });
        menu.Items.Add(new Separator());
        AddMenuAction(menu, "RefreshConfigurationMenuItem", "Refresh configuration", RefreshProfiles);
        AddMenuAction(menu, "CompactConversationMenuItem", "Compact conversation", CompactSession);
        AddMenuAction(menu, "RetryConnectionMenuItem", "Retry connection", ReconcileCurrentJob);
        menu.Items.Add(new Separator());
        AddMenuAction(menu, "ChangeConversationProject", "Change project…", () => ChangeConversationProjectAsync());
        AddMenuAction(menu, "OpenKnowledgeSettings", "Open Configuration…", OpenConnectionConfigurationAsync,
            "Opens the general Configuration window for this Worker. Choose the settings page you need there.");
        return menu;
    }

    private void UpdateConversationLayout()
    {
        if (conversationLayout is null || workspaceColumn is null || workspaceSplitterColumn is null || activitySplitterColumn is null || activityColumn is null || workspaceRail is null || activityRail is null) return;
        var slotWidth = LayoutInformation.GetLayoutSlot(conversationLayout).Width -
            conversationLayout.Margin.Left - conversationLayout.Margin.Right;
        var width = slotWidth > 0 ? Math.Min(conversationLayout.ActualWidth, slotWidth) : conversationLayout.ActualWidth;
        if (width <= 0) return;
        var compactHeight = conversationLayout.ActualHeight is > 0 and < 560;
        var showActivity = activityRequested && desktopSidebarTabs.HasItems;
        var showWorkspace = workspaceRequested && (width >= WorkspaceAutomaticWidth || compactWorkspaceOpen) && (!showActivity || width >= ActivitySidebarAutomaticWidth);
        workspaceRail.Visibility = showWorkspace ? Visibility.Visible : Visibility.Collapsed;
        activityRail.Visibility = showActivity ? Visibility.Visible : Visibility.Collapsed;
        var activityWidth = showActivity ? Math.Clamp(activityPreferredWidth, 280, Math.Max(280, Math.Min(520, width * .42))) : 0;
        // Title space is limited by the current viewport and other visible panes, not an arbitrary
        // sidebar cap. Keep the preferred width when a smaller window temporarily constrains it.
        var workspaceMaximum = Math.Max(208, width - MinimumConversationWidth - 7 - (showActivity ? activityWidth + 7 : 0));
        workspaceColumn.MinWidth = showWorkspace ? 208 : 0; workspaceColumn.MaxWidth = showWorkspace ? workspaceMaximum : 0;
        workspaceColumn.Width = new GridLength(showWorkspace ? Math.Clamp(workspacePreferredWidth, 208, workspaceMaximum) : 0);
        workspaceSplitterColumn.Width = new GridLength(showWorkspace ? 7 : 0);
        activityColumn.MinWidth = showActivity ? 280 : 0; activityColumn.MaxWidth = showActivity ? 520 : 0;
        activityColumn.Width = new GridLength(activityWidth);
        activitySplitterColumn.Width = new GridLength(showActivity ? 7 : 0);
        if (workspaceDivider is not null) workspaceDivider.Visibility = workspaceRail.Visibility;
        if (pluginDivider is not null) pluginDivider.Visibility = activityRail.Visibility;
        if (workspaceVisibilityButton is not null) workspaceVisibilityButton.ToolTip = showWorkspace ? "Hide projects and conversations" : "Show projects and conversations";
        if (activityVisibilityButton is not null)
        {
            activityVisibilityButton.IsEnabled = desktopSidebarTabs.HasItems;
            activityVisibilityButton.Content = showActivity ? "Hide panels" : "Panels";
            activityVisibilityButton.ToolTip = !desktopSidebarTabs.HasItems ? "No plugin panels are available." : showActivity ? "Hide plugin panels" : "Show plugin panels";
            AutomationProperties.SetName(activityVisibilityButton, "Show or hide plugin panels");
            activityVisibilityButton.Background = showActivity ? Ui.Paper : Ui.Surface;
        }
        var usable = width - (showWorkspace ? workspaceColumn.Width.Value + 7 : 0) - (showActivity ? activityColumn.Width.Value + 7 : 0);
        if (projectKnowledgeControls is not null) projectKnowledgeControls.Visibility = usable >= 850 && ConversationProjectId is not null ? Visibility.Visible : Visibility.Collapsed;
        if (composerCaption is not null) composerCaption.Visibility = Visibility.Collapsed;
        prompt.MinHeight = compactHeight ? 48 : 72;
        if (composerPreferredHeight is null) prompt.MaxHeight = compactHeight ? 96 : 160;
        UpdateComposerPluginTools(usable); UpdateComposerOptions(usable); UpdateDesktopInputGuidanceLayout();
    }

    private void UpdateComposerOptions(double width)
    {
        if (composerOptionsPanel is null || composerHeader is null || composerOptionsButton is null || composerOptionsPopup is null) return;
        var popup = width < 660;
        if (popup && composerOptionsPanel.Parent is Grid grid)
        {
            grid.Children.Remove(composerOptionsPanel);
            var scroll = new ScrollViewer { Content = composerOptionsPanel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, MaxHeight = 260, Width = 390 };
            composerOptionsPopup.Child = new Border { Child = scroll, Padding = new Thickness(12), Background = Ui.Surface,
                BorderBrush = Ui.Line, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6) };
        }
        else if (!popup && composerOptionsPanel.Parent is ScrollViewer scroll)
        {
            composerOptionsPopup.IsOpen = false; scroll.Content = null; composerOptionsPopup.Child = null;
            composerHeader.Children.Add(composerOptionsPanel);
        }
        composerOptionsButton.Visibility = popup ? Visibility.Visible : Visibility.Collapsed;
        if (!popup)
        {
            Grid.SetRow(composerOptionsPanel, 0); Grid.SetColumn(composerOptionsPanel, 1);
            Grid.SetColumnSpan(composerOptionsPanel, 1);
            composerOptionsPanel.MaxWidth = Math.Max(280, width - 160); composerOptionsPanel.Margin = new Thickness(0);
        }
        else { composerOptionsPanel.MaxWidth = 390; composerOptionsPanel.Margin = new Thickness(0); }
    }
}
