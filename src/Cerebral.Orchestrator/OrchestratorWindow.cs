using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Automation;
using System.Windows.Input;
using System.Windows.Media;
using Cerebral.Client;
using Cerebral.Contracts.Workflows;
using Cerebral.Core;
using Cerebral.Desktop;
using Cerebral.Desktop.Plugins;

namespace Cerebral.Orchestrator;

public sealed partial class OrchestratorWindow : ConnectedWindow
{
    private const double WorkspaceAutomaticWidth = 880;
    private const double ActivitySidebarAutomaticWidth = 1320;
    private readonly ListBox conversations = new() { DisplayMemberPath = "Title", BorderBrush = Ui.Line };
    private readonly StackPanel messages = new();
    private readonly ScrollViewer transcript;
    private readonly TextBox prompt = Ui.Input("", true);
    private readonly TextBlock sessionHeading = Ui.Text("New conversation", 22);
    private readonly TextBlock sessionState = Ui.Text("Describe what you would like to do. Cerebral coordinates the work.", 12, Ui.Muted);
    private readonly TextBlock agentSummary = Ui.Text("Connecting to Cerebral…", 12, Ui.Muted);
    private readonly StackPanel questionPanel = new();
    private readonly TextBox liveOutput = Ui.Input("", true);
    private readonly Expander liveDetails = new() { Header = "Generation details", Visibility = Visibility.Collapsed, Margin = new Thickness(0, 0, 0, 8) };
    private readonly TextBlock liveLabel = Ui.Text("", 12, Ui.Muted);
    private readonly TextBox filePath = Ui.Input();
    private readonly TextBox fileContent = Ui.Json();
    private readonly TextBox fileListing = Ui.Json();
    private readonly CheckBox sharedFiles = new() { Content = "Shared files", Margin = new Thickness(0, 7, 16, 0) };
    private readonly ListBox fileEntries = new() { DisplayMemberPath = "Label", BorderBrush = Ui.Line };
    private readonly TextBlock fileInfo = Ui.Text("Files are read and saved on the agent's execution worker within its allowed roots.", 12, Ui.Muted);
    private readonly TextBlock fileLocation = Ui.Text("Choose a project or folder to browse.", 12, Ui.Muted);
    private Button? fileHomeButton;
    private Button? fileUpButton;
    private Button? fileRootsButton;
    private Button? fileOpenButton;
    private Expander? fileListingDetails;
    private FrameworkElement? fileWorkspaceRoot;
    private readonly ListBox toolList = new() { DisplayMemberPath = "Name", MinWidth = 230, BorderBrush = Ui.Line };
    private readonly TextBox toolArguments = Ui.Json("{}");
    private readonly TextBox toolOutput = Ui.Json();
    private readonly TextBlock toolDescription = Ui.Text("Discover the tools available to Cerebral, then select one to inspect its inputs.", 13, Ui.Muted);
    private readonly TextBlock toolAvailability = Ui.Text("Tool availability follows Cerebral's saved grants in Pegasus Cerebral Configuration.", 12, Ui.Muted);
    private Grid? conversationLayout;
    private ColumnDefinition? workspaceColumn;
    private ColumnDefinition? workspaceSplitterColumn;
    private ColumnDefinition? activitySplitterColumn;
    private ColumnDefinition? activityColumn;
    private FrameworkElement? workspaceRail;
    private FrameworkElement? activityRail;
    private Grid? conversationCenter;
    private StackPanel? conversationComposer;
    private WrapPanel? composerOptionsPanel;
    private Border? questionHost;
    private Border? conversationHeaderStrip;
    private FrameworkElement? projectKnowledgeControls;
    private TextBlock? composerCaption;
    private Button? workspaceVisibilityButton;
    private Button? activityVisibilityButton;
    private Button? compactActionsButton;
    private readonly CheckBox enterSends = new() { Content = "Enter sends", IsChecked = false, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
    private double activityPreferredWidth = 340;
    private double workspacePreferredWidth = 248;
    private bool workspaceRequested = true;
    private bool activityRequested;
    private bool compactWorkspaceOpen;
    private TaskSession? session;
    private AgentProfile? leadProfile;
    private string leadConfigurationError = "Connect to a service with one lead Cerebral agent configured in Pegasus Cerebral Configuration.";
    private List<AgentProfile> profiles = [];
    private List<ModelRecord> configuredModels = [];
    private int queuedJobCount;
    private List<AgentJob> activeJobs = [];
    private bool selecting;
    private SubmitJobRequest? pendingSubmission;
    private string? openFileHash;
    private string? openFilePath;
    private string? openFileProfileId;
    private bool openFileTruncated;
    private string? liveJobId;
    private readonly Dictionary<string, string> drafts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> answerDrafts = new(StringComparer.Ordinal);
    private int loadedQuestionCount;
    private string sharedDirectory = "";
    private string projectsDirectory = "";
    private string modelDirectory = "";
    private string toolsDirectory = "";
    private string sharedFilesHost = "local";
    private Button? stopButton;
    private bool stopRequested;
    private List<UserQuestion> loadedQuestions = [];

    public OrchestratorWindow() : this(null, null) { }

    internal OrchestratorWindow(DesktopPluginCatalog? pluginCatalog, string? pluginDataRoot,
        string? composerDraftDirectory = null, DesktopInstancePaths? composerDraftPaths = null,
        ExistingWorkerDesktopOptions? attachedComposerStartup = null)
        : base("Orchestrator", "One lead conversation, configured helpers, and direct development tools.", compactHeader: true,
            persistWindowPreferences: false)
    {
        desktopPluginCatalog = pluginCatalog ?? new DesktopPluginCatalog();
        desktopPluginDataRoot = pluginDataRoot is null ? null : Path.GetFullPath(pluginDataRoot);
        MinWidth = 720;
        enterSends.IsChecked = UiPreferenceStore.ReadBool("Cerebral.EnterSends", false);
        workspaceRequested = UiPreferenceStore.ReadBool("Cerebral.NavigationVisible", true);
        activityRequested = UiPreferenceStore.ReadBool("Cerebral.PanelsVisible", false);
        InitializeConversationTokenBalance();
        transcript = new ScrollViewer
        {
            Content = messages,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            PanningMode = PanningMode.VerticalOnly,
            Padding = new Thickness(3, 0, 10, 0)
        };
        prompt.MinHeight = 80; prompt.MaxHeight = 180; UpdateComposerKeyHint();
        TextContextMenus.EnableWritingAssistance(prompt);
        // Preview runs before the multiline TextBox consumes its editing gestures.
        prompt.PreviewKeyDown += (_, e) =>
        {
            if (!e.Handled && TryHandleComposerEnter(prompt, e.Key, Keyboard.Modifiers,
                enterSends.IsChecked == true, composerCompositionActive, e.IsRepeat, () => Run(Send)))
                e.Handled = true;
        };
        PreviewMouseDown += (_, e) =>
        {
            if (TryHandleConversationHistoryMouse(e)) return;
            if (e.OriginalSource is not DependencyObject source) return;
            PassiveDesktopInteraction.TryRun("activity click", () =>
            {
                // Resume an activity viewport without taking keyboard focus from the composer or a
                // dictation plugin that owns it. The existing Send exception below remains authoritative.
                ResumeActivityFollowAfterOutsideClick(source);
                nativeChat?.ResumeActivityFollowAfterOutsideClick(source);
                workflowWorkspace?.ResumeActivityFollowAfterOutsideClick(source);
                if (composerSendButton is not null && ReferenceEquals(NavigationAncestor<Button>(source), composerSendButton))
                {
                    // The Send click must reach text-entry submission participants before a focus-loss
                    // notification can cancel their pending edit (for example, local dictation).
                    composerSendButton.Focusable = false;
                    return;
                }
                if (ShouldReleaseTextEntryFocus(source, Keyboard.FocusedElement)) Keyboard.ClearFocus();
            }, message => SetStatus(message, true));
        };
        enterSends.Checked += (_, _) => { SavePortableInterfaceBool(InterfacePreferencePolicy.EnterSends, "Cerebral.EnterSends", true); UpdateComposerKeyHint(); };
        enterSends.Unchecked += (_, _) => { SavePortableInterfaceBool(InterfacePreferencePolicy.EnterSends, "Cerebral.EnterSends", false); UpdateComposerKeyHint(); };
        var conversationItemStyle = Ui.ExtendStyle<ListBoxItem>();
        conversationItemStyle.Setters.Add(new Setter(AutomationProperties.NameProperty, new System.Windows.Data.Binding("Title")));
        conversationItemStyle.Setters.Add(new Setter(ToolTipProperty, new System.Windows.Data.Binding("Title")));
        conversations.ItemContainerStyle = conversationItemStyle;
        AutomationProperties.SetAutomationId(conversations, "ConversationList");
        AutomationProperties.SetName(conversations, "Saved conversations");
        AutomationProperties.SetAutomationId(prompt, "MessageComposer");
        AutomationProperties.SetName(prompt, "Message to Cerebral");
        AutomationProperties.SetAutomationId(transcript, "ConversationTranscript");
        AutomationProperties.SetAutomationId(sessionState, "ConversationStatus");
        AutomationProperties.SetName(sessionState, "Conversation status");
        AutomationProperties.SetName(sessionHeading, "Conversation title");
        AutomationProperties.SetAutomationId(agentSummary, "OrchestratorSummary");
        AutomationProperties.SetAutomationId(questionPanel, "ConversationQuestions");
        AutomationProperties.SetAutomationId(filePath, "FilePath");
        AutomationProperties.SetName(filePath, "Current folder or file path");
        AutomationProperties.SetAutomationId(fileLocation, "FileLocation");
        filePath.TextChanged += (_, _) => { InvalidateFileBrowserRead(); UpdateFileLocation(); };
        AutomationProperties.SetAutomationId(toolList, "ToolList");
        AutomationProperties.SetAutomationId(toolArguments, "ToolArguments");
        sharedFiles.Checked += (_, _) => ResetFileSource(); sharedFiles.Unchecked += (_, _) => ResetFileSource();
        conversations.SelectionChanged += (_, _) => { if (!selecting && conversations.SelectedItem is TaskSession selected) Run(() => SelectSession(selected.Id)); };
        liveOutput.IsReadOnly = true; liveOutput.MaxHeight = 180; liveOutput.BorderBrush = Ui.Line;
        liveDetails.Content = liveOutput; liveDetails.ToolTip = "Raw structured model output for diagnostics. Completed responses and tool results appear in the conversation.";
        AutomationProperties.SetAutomationId(liveDetails, "GenerationDetails");
        AddPage("chat", "Chat", ConversationPage());
        AddPage("work", "Work", WorkflowWorkspacePage());
        AddPage("files", "Files", FilesPage());
        AddPage("tools", "Tools", ToolsPage());
        ComposeApplicationSurface(BuildShell);
        InitializeMainChatWorkflowChoice();
        RenderWelcome();
        InitializeDesktopInteraction();
        InitializeNativePluginManagement();
        InitializeComposerDrafts(composerDraftDirectory, composerDraftPaths, attachedComposerStartup);
        InitializeMainLayout(attachedComposerStartup is not null || pluginCatalog is null && pluginDataRoot is null);
    }

    private static bool TryHandleComposerEnter(TextBox editor, Key key, ModifierKeys modifiers,
        bool sendOnEnter, bool compositionActive, bool isRepeat, Action send)
    {
        // IME confirmation, unsupported shortcuts and read-only inputs keep native handling.
        if (key != Key.Enter || compositionActive || !editor.IsEnabled || editor.IsReadOnly ||
            (modifiers & ~(ModifierKeys.Control | ModifierKeys.Shift)) != ModifierKeys.None) return false;
        var control = modifiers.HasFlag(ModifierKeys.Control);
        var shouldSend = !modifiers.HasFlag(ModifierKeys.Shift) && sendOnEnter != control;
        if (shouldSend)
        {
            // A held send key must not create another request. Run(Send) retains all admission guards.
            if (!isRepeat) send();
            return true;
        }
        // Ctrl+Enter has no native TextBox newline gesture. Group this selection edit into
        // one undo unit, preserving the editor's character limit without taking focus.
        if (!editor.AcceptsReturn) return false;
        var newline = Environment.NewLine;
        if (editor.MaxLength > 0 && editor.Text.Length - editor.SelectionLength > editor.MaxLength - newline.Length)
            return true;
        var start = editor.SelectionStart;
        editor.BeginChange();
        try
        {
            editor.SelectedText = newline;
            editor.Select(start + newline.Length, 0);
        }
        finally { editor.EndChange(); }
        return true;
    }

    private void UpdateComposerKeyHint()
    {
        prompt.ToolTip = enterSends.IsChecked == true
            ? "Enter sends. Ctrl+Enter adds a new line."
            : "Ctrl+Enter sends. Enter adds a new line.";
        AutomationProperties.SetName(enterSends, enterSends.IsChecked == true ? "Enter sends messages" : "Control Enter sends messages");
    }
    private UIElement FilesPage()
    {
        var root = new DockPanel { Margin = new Thickness(16) }; fileWorkspaceRoot = root;
        var header = new StackPanel();
        header.Children.Add(Ui.Text("Workspace files", 23)); header.Children.Add(fileInfo);
        fileHomeButton = Ui.Button("Home", () => Run(NavigateFileHome));
        fileUpButton = Ui.Button("Up", () => Run(NavigateFileUp));
        fileRootsButton = Ui.Button("Project roots", () => Run(ShowRoots));
        AutomationProperties.SetAutomationId(fileHomeButton, "WorkspaceFilesHome");
        AutomationProperties.SetAutomationId(fileUpButton, "WorkspaceFilesUp");
        AutomationProperties.SetAutomationId(fileRootsButton, "WorkspaceProjectRoots");
        fileHomeButton.ToolTip = "Open the current project's folder, or the configured project folder when no project is selected.";
        fileUpButton.ToolTip = "Open the parent folder without leaving the agent's configured file roots.";
        fileRootsButton.ToolTip = "Show registered projects and the folders this agent can browse.";
        header.Children.Add(Ui.Bar(fileHomeButton, fileUpButton, fileRootsButton, sharedFiles));
        fileLocation.Margin = new Thickness(0, 0, 0, 4); fileLocation.TextTrimming = TextTrimming.CharacterEllipsis;
        fileLocation.ToolTip = fileLocation.Text; header.Children.Add(fileLocation);
        filePath.MinWidth = 0; filePath.ToolTip = "Enter a folder to list or a text file to read."; header.Children.Add(filePath);
        fileOpenButton = Ui.Button("Open selected", () => Run(OpenSelectedFileEntry));
        AutomationProperties.SetAutomationId(fileOpenButton, "OpenWorkspaceFile");
        fileOpenButton.ToolTip = "Open the selected folder in Cerebral or the selected local file with its Windows application.";
        var bar = Ui.Bar(fileOpenButton, Ui.Button("Refresh folder", () => Run(OpenCurrentFileFolder)), Ui.Button("Read file", () => Run(ReadFile)),
            Ui.Button("Save edits", () => Run(SaveFile), true), Ui.Button("Save as…", () => Run(SaveAs)),
            Ui.Button("New folder…", () => Run(NewFolder)), Ui.Button("Preview Markdown", PreviewFile),
            Ui.Button("Copy to worker…", () => Run(TransferTextFile)));
        bar.Margin = new Thickness(0, 8, 0, 8); header.Children.Add(bar); DockPanel.SetDock(header, Dock.Top); root.Children.Add(header);
        var split = new Grid(); split.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star), MinWidth = 230 });
        split.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3, GridUnitType.Star), MinWidth = 300 });
        fileListing.IsReadOnly = true; fileListing.Height = 110;
        fileListingDetails = new Expander { Header = "Technical details", Content = fileListing, IsExpanded = false, Margin = new Thickness(0, 7, 0, 0) };
        AutomationProperties.SetAutomationId(fileListingDetails, "WorkspaceFileDetails");
        var browser = new DockPanel { Margin = new Thickness(0, 0, 12, 0) };
        var browserHeading = Ui.Text("Folder contents", 13); browserHeading.FontWeight = FontWeights.SemiBold; browserHeading.Margin = new Thickness(0, 0, 0, 5);
        DockPanel.SetDock(browserHeading, Dock.Top); browser.Children.Add(browserHeading);
        DockPanel.SetDock(fileListingDetails, Dock.Bottom); browser.Children.Add(fileListingDetails); browser.Children.Add(fileEntries); split.Children.Add(browser);
        ConfigureFileEntryInteraction();
        var editor = new DockPanel(); var editorHeading = Ui.Text("Document editor", 13); editorHeading.FontWeight = FontWeights.SemiBold; editorHeading.Margin = new Thickness(0, 0, 0, 5);
        DockPanel.SetDock(editorHeading, Dock.Top); editor.Children.Add(editorHeading); editor.Children.Add(fileContent);
        Grid.SetColumn(editor, 1); split.Children.Add(editor); root.Children.Add(split); UpdateFileLocation(); return root;
    }
    private UIElement ToolsPage()
    {
        var root = new DockPanel { Margin = new Thickness(20) }; var header = new StackPanel(); header.Children.Add(Ui.Text("Cerebral tools", 23));
        header.Children.Add(Ui.Text("These direct tools use the current conversation’s Cerebral grants and selected folders. Select a tool to inspect its inputs before executing it.", 13, Ui.Muted));
        header.Children.Add(toolAvailability);
        header.Children.Add(Ui.Bar(Ui.Button("Discover tools", () => Run(DiscoverTools)), Ui.Button("Execute selected", () => Run(ExecuteTool), true)));
        DockPanel.SetDock(header, Dock.Top); root.Children.Add(header);
        var split = new Grid(); split.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(260) }); split.ColumnDefinitions.Add(new ColumnDefinition());
        toolList.Margin = new Thickness(0, 0, 15, 0); split.Children.Add(toolList);
        var content = new Grid(); content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); content.RowDefinitions.Add(new RowDefinition()); content.RowDefinitions.Add(new RowDefinition());
        content.Children.Add(toolDescription); toolArguments.Margin = new Thickness(0, 0, 0, 10); Grid.SetRow(toolArguments, 1); content.Children.Add(toolArguments);
        toolOutput.IsReadOnly = true; Grid.SetRow(toolOutput, 2); content.Children.Add(toolOutput); Grid.SetColumn(content, 1); split.Children.Add(content); root.Children.Add(split);
        toolList.SelectionChanged += (_, _) =>
        {
            if (toolList.SelectedItem is not ToolDescriptor tool) return;
            var execution = IsPao(tool) ? "\nExecution level: Cerebral selects the signed-in user host or its private Cortex system-service session. Saved filesystem, Git, process and diagnostics capabilities still gate each operation." : "";
            toolDescription.Text = tool.Description + $"\n\nRequired grant: {tool.Capability}{execution}\n\nInput schema: " + tool.InputSchema;
            toolArguments.Text = "{}";
        };
        return root;
    }

    protected override async Task ConnectedAsync(HostInfo info)
    {
        ResetSystemWorkMetrics(info);
        ResetNativePluginManagementLiveAdmission();
        interfaceOutcomeServiceKey = ServiceIsLocal ? DesktopInterfaceOutcomeJournal.ServiceKey(info) : null;
        ReportStartupProgress("Settings", "Loading interface preferences and agent configuration…");
        await HydratePortableInterfacePreferencesAsync();
        await HydrateMainLayoutAsync(info);
        await RefreshProfiles();
        ReportStartupProgress("Conversations", "Loading recent conversations and active work…");
        await RefreshJobs(); await RefreshSessions();
        ReportStartupProgress("Workflows", "Hydrating workflow and conversation surfaces…");
        if (workflowWorkspace?.IsLoaded == true) await workflowWorkspace.RefreshFromConnectionAsync();
        if (nativeChat?.Snapshot?.RunId is not null) await nativeChat.RefreshFromConnectionAsync();
        if (session is not null) await SelectSession(session.Id);
        await RestoreComposerDraftSelectionAsync();
        await RefreshConversationWorkspaceAsync();
        await RefreshMainChatWorkflowAvailabilityAsync();
        // Plugin panels may render before the asynchronous Worker connection is ready. Re-run their
        // connection-dependent projections even when the selected conversation itself did not change.
        desktopPluginHost?.PublishConversationChanged(force: true);
    }

    internal async Task<DesktopStartupOutcome> WaitForInitialReadyAsync(CancellationToken cancellationToken)
    {
        var outcome = await InitialConnectionSettled.WaitAsync(cancellationToken);
        await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle, cancellationToken);
        UpdateLayout();
        await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle, cancellationToken);
        return outcome;
    }
    protected override async Task<object> InspectLoadedStateAsync(string? sessionId)
    {
        var initialProfile = leadProfile;
        var selected = sessionId ?? (conversations.ItemsSource as IEnumerable<TaskSession>)?.FirstOrDefault()?.Id;
        if (selected is not null) await SelectSession(selected);
        await DiscoverTools();
        return new { connected = IsConnected, initialProfile = initialProfile?.Id, initialRole = initialProfile?.Role,
            conversations = conversations.Items.Count, profiles = profiles.Count, jobs = queuedJobCount,
            selectedSession = CurrentConversationId, messages = session?.Messages.Count ?? nativeChat?.Snapshot?.Events.Count ?? 0, renderedMessages = messages.Children.Count,
            questions = loadedQuestionCount, knowledgeProposals = loadedQuestions.Count(x => x.KnowledgeProposalId is not null), tools = toolList.Items.Count, projectId = ConversationProjectId, knowledge = projectKnowledgeSummary.Text, state = sessionState.Text[..Math.Min(1000, sessionState.Text.Length)] };
    }
    private async Task RefreshProfiles()
    {
        var hostSettings = await Get<HostSettings>("settings.get"); sharedDirectory = hostSettings.SharedDirectory; sharedFilesHost = hostSettings.SharedFilesWorkerId;
        if (systemWorkMetricsInstallationId == hostSettings.InstallationId) CaptureRemoteSlotRegistrations(hostSettings);
        else ResetRemoteSlotMetrics();
        projectsDirectory = hostSettings.ProjectsDirectory; modelDirectory = hostSettings.ModelDirectory; toolsDirectory = hostSettings.ToolsDirectory;
        configuredModels = await Get<List<ModelRecord>>("models.list");
        profiles = await Get<List<AgentProfile>>("agents.list");
        var leads = profiles.Where(x => x.Role == "lead" && x.WorkerId == "local").ToList();
        leadProfile = leads.Count == 1 ? leads[0] : null;
        leadConfigurationError = leads.Count == 0
            ? "Configure one lead Cerebral agent in Pegasus Cerebral Configuration, then refresh configuration."
            : "More than one lead Cerebral agent is configured. Choose one lead in Pegasus Cerebral Configuration, then refresh configuration.";
        if (session is not null && !IsUserConversation(session)) NewConversation();
        toolList.ItemsSource = null; toolArguments.Text = "{}"; toolOutput.Clear();
        var profile = TryConversationProfile();
        if (profile is null || !HasSharedGrant(profile, "read") && !HasSharedGrant(profile, "write")) sharedFiles.IsChecked = false;
        ResetFileSource(); UpdateAgentSummary(); RefreshConversationTokenBalance();
        if (conversations.ItemsSource is not null) await RefreshSessions();
        await RefreshProjectKnowledgeAsync(force: true);
    }
    private async Task RefreshProfileRoutesAfterSettingsAsync()
    {
        // Settings.Save can move the configured project root and rewrite matching local profile
        // grants in the same transaction. It publishes settings.updated, not agents.updated.
        // Refresh the cached route without rebuilding the conversation or its composer draft.
        if (!IsConnected) return;
        var owner = Client;
        var connection = ServiceConnectionIdentity;
        var host = await owner.GetAsync<HostSettings>("settings.get", cancellationToken: Lifetime.Token);
        var latestProfiles = await owner.GetAsync<List<AgentProfile>>("agents.list", cancellationToken: Lifetime.Token);
        if (!IsConnected || connection != ServiceConnectionIdentity || !ReferenceEquals(owner, Client)) return;
        var previousProjects = projectsDirectory;
        var rootChanged = !string.Equals(previousProjects, host.ProjectsDirectory, StringComparison.OrdinalIgnoreCase);
        var profilesChanged = profiles.Count != latestProfiles.Count ||
            profiles.Any(old => latestProfiles.FirstOrDefault(current => current.Id == old.Id)?.Revision != old.Revision);
        sharedDirectory = host.SharedDirectory;
        sharedFilesHost = host.SharedFilesWorkerId;
        projectsDirectory = host.ProjectsDirectory;
        modelDirectory = host.ModelDirectory;
        toolsDirectory = host.ToolsDirectory;
        if (!rootChanged && !profilesChanged) return;
        profiles = latestProfiles;
        var leads = profiles.Where(x => x.Role == "lead" && x.WorkerId == "local").ToList();
        leadProfile = leads.Count == 1 ? leads[0] : null;
        leadConfigurationError = leads.Count == 0
            ? "Configure one lead Cerebral agent in Pegasus Cerebral Configuration, then refresh configuration."
            : "More than one lead Cerebral agent is configured. Choose one lead in Pegasus Cerebral Configuration, then refresh configuration.";
        if (rootChanged && string.Equals(filePath.Text, previousProjects, StringComparison.OrdinalIgnoreCase))
        {
            InvalidateFileBrowserRead();
            fileEntries.ItemsSource = null;
            fileListing.Clear();
            filePath.Text = host.ProjectsDirectory;
        }
        UpdateAgentSummary();
        RefreshConversationTokenBalance();
        await RefreshFreshNormalChatWorkflowAfterProfilesAsync(Lifetime.Token);
    }
    private string? NativeChatProfileId => IsNativeChat ?
        (nativeChat?.Snapshot is { } selected && selected.ConversationId == durableConversationId ? selected.Run?.ProfileId : null)
        ?? durableConversations.GetValueOrDefault(durableConversationId!)?.ProfileId
        ?? (attachmentReservedOwner is { } reserved && reserved.ConversationId == durableConversationId ? reserved.ProfileId : null)
        ?? (retainedNativeSubmission is { } retained && RetainedNativeOwnerCurrent() ? retained.ProfileId : null)
        ?? InitialAcceptedProfileId : null;
    private AgentProfile? TryConversationProfile() => session is not null ? profiles.FirstOrDefault(x => x.Id == session.ProfileId && x.Role == "lead") :
        NativeChatProfileId is { } savedProfile ? profiles.FirstOrDefault(x => x.Id == savedProfile) : FreshChatSelectedProfile();
    private string ConversationProfileError => NativeChatProfileId is { } savedProfile
        ? $"This conversation belongs to profile '{savedProfile}', which is unavailable. Restore that profile in Configuration or start a new chat with the current Cerebral profile."
        : freshChatDraft?.Selection.ProfileId is { } draftProfile
            ? $"The selected profile '{draftProfile}' or its persisted revision changed. Start a new chat to select it again."
            : leadConfigurationError;
    private AgentProfile ConversationProfile() => TryConversationProfile() ?? throw new InvalidOperationException(ConversationProfileError);
    private void UpdateAgentSummary()
    {
        if (TryConversationProfile() is not AgentProfile profile)
        {
            agentSummary.Text = ConversationProfileError;
            if (session is null) sessionState.Text = ConversationProfileError;
            return;
        }
        var model = profile.ModelWorkerId == "local" ? configuredModels.FirstOrDefault(x => x.Id == profile.ModelId) : null;
        var helpers = profiles.Count(x => x.Role == "helper");
        agentSummary.Text = $"{profile.Name} · {helpers} helper{(helpers == 1 ? "" : "s")} available · {model?.Name ?? profile.ModelId}" +
            (model is not null ? $" ({model.State})" : "");
        agentSummary.ToolTip = $"Context {profile.ContextTokens:N0} tokens · execution {profile.WorkerId} · inference {profile.ModelWorkerId}";
        if (IsNativeChat) UpdateNativeChatState();
        else if (session is null) sessionState.Text = "Describe what you would like to do. Cerebral coordinates the work.";
    }
    private bool IsUserConversation(TaskSession candidate) =>
        profiles.Any(x => x.Id == candidate.ProfileId && x.Role == "lead") &&
        !activeJobs.Any(x => x.SessionId == candidate.Id && x.ParentJobId is not null);
    private async Task RefreshSessions()
    {
        if (!showingLegacyNavigation) { await RefreshDurableConversationsAsync(); return; }
        // The previous execution route is explicit and bounded. Open one exact session only when selected.
        var all = activeJobs.OrderByDescending(x => x.CreatedAt).DistinctBy(x => x.SessionId).Take(40)
            .Select(x => new TaskSession { Id = x.SessionId, Title = "Previous · " + x.Prompt[..Math.Min(65, x.Prompt.Length)].Replace('\n', ' '),
                ProfileId = x.ProfileId, UpdatedAt = x.FinishedAt ?? x.CreatedAt }).Where(IsUserConversation).ToList(); selecting = true;
        try { conversations.ItemsSource = all; conversations.SelectedItem = all.FirstOrDefault(x => x.Id == session?.Id); }
        finally { selecting = false; }
        await RefreshNavigationAsync(all);
    }
    private async Task RefreshJobs()
    {
        var queue = await Get<List<AgentJob>>("queue.list");
        var history = await Get<List<AgentJob>>("jobs.history");
        activeJobs = queue.Concat(history).DistinctBy(x => x.Id).ToList(); queuedJobCount = queue.Count;
        UpdateSessionState();
        foreach (var view in turnActivity.Values.Where(x => x.SessionId == session?.Id)) UpdateTurnActivity(view);
    }
    private static bool Active(AgentJob job) => job.Status is "queued" or "running" or "waiting" or "waiting-human" or "waiting-helper" or "remote-waiting" or "remote-unconfirmed";
    private AgentJob? CurrentJob() => activeJobs.FirstOrDefault(x => x.SessionId == session?.Id && Active(x));
    private void UpdateSessionState()
    {
        if (IsNativeChat) { UpdateNativeChatState(); return; }
        if (stopButton is not null) stopButton.IsEnabled = session is not null && !stopRequested;
        if (session is null) return;
        var current = CurrentJob(); var latest = activeJobs.FirstOrDefault(x => x.SessionId == session.Id);
        desktopPluginHost?.PublishConversationChanged();
        sessionState.ToolTip = $"Session {session.Id}";
        sessionState.Text = current is null ? latest?.Status switch
        {
            "failed" => "Work failed · " + (latest.Error ?? "See Activity for details."),
            "incomplete" => "Work is incomplete · " + (latest.Error ?? "Review the open failures in Activity."),
            "interrupted" => "Work was interrupted · " + (latest.Error ?? "See Activity for the last recorded outcome."),
            "cancelled" => "Work cancelled. You can send a new message to continue.",
            _ => "Ready for your next message."
        } : current.Status switch
        {
            "queued" => "Queued · " + (current.WaitingReason ?? "Waiting for an execution slot."),
            "waiting-human" => "Waiting for your answer in this conversation.",
            "waiting-helper" => "Cerebral is waiting for delegated work. Follow progress in Activity.",
            "remote-waiting" or "remote-unconfirmed" => "Waiting for worker connection · " + current.WaitingReason,
            _ => current.Kind == "compact" ? "Compacting this conversation’s working context…" : "Cerebral is working…"
        };
    }
    private void NewConversation()
    {
        // This handler is also used directly by the tray and navigation button, outside Run's gate.
        try { NewConversationInProject(ProjectForNewConversation()); }
        catch (Exception error) { SetStatus(error.Message, true); }
    }
    internal void NewConversationFromTray()
    {
        DesktopApplicationInstance.Show(this);
        NewConversation();
        SelectPage("chat");
    }
    private void NewConversationInProject(string? projectId, FreshChatSelection? selection = null,
        string? restoredLocalId = null, bool recordHistory = true, int? responseSharePercent = null)
    {
        EnsureNativeNavigationAllowed();
        ResetTeamForChangedMain();
        if (recordHistory) RememberCurrentConversationVisit();
        var protectedStartupSelection = composerDraftProtectedSelection is not null;
        composerDraftProtectedSelection = null;
        composerDraftSelectionError = null;
        conversationSelectionVersion++; transcriptRestorePending = false;
        showingLegacyNavigation = readingOlderChats = showingAllLoadedChats = false; durableNavigationVersion++;
        draftProjectId = projectId;
        if (CurrentConversationId is { } prior && prior != restoredLocalId &&
            (!protectedStartupSelection || prompt.Text.Length > 0))
            RememberComposerDraft(prior, prompt.Text);
        session = null; BeginNativeChat(selection, restoredLocalId); ResetDesktopConversation(); pendingSubmission = null; liveJobId = null; selecting = true; conversations.SelectedItem = null; selecting = false;
        selectedResponseSharePercent = responseSharePercent;
        sessionHeading.Text = "New conversation"; messages.Children.Clear(); ResetQuestionDesktopTextEntries(); questionPanel.Children.Clear(); loadedQuestionCount = 0; UpdateQuestionHostVisibility();
        liveOutput.Clear(); liveDetails.Visibility = Visibility.Collapsed; liveDetails.IsExpanded = false; liveLabel.Text = "";
        // The restored ID is already current. Suppress draft bookkeeping while clearing the old
        // composer, or TextChanged would delete the target draft before its text is read.
        changingComposerDraft = true;
        try { prompt.Clear(); }
        finally { changingComposerDraft = false; }
        if (restoredLocalId is not null) RestoreComposerDraftText(restoredLocalId);
        // Clearing an already empty composer raises no TextChanged event. Save the new
        // selection explicitly after BeginNativeChat, including its project and new state.
        if (CurrentConversationId is { } current)
        {
            RememberComposerDraft(current, prompt.Text);
            selectedNavigationId = NavigationIdForConversation(current);
            if (recordHistory) RememberCurrentConversationVisit();
        }
        interfaceRequests.Children.Clear(); loadedQuestions.Clear(); RenderWelcome(); UpdateAgentSummary(); RefreshConversationTokenBalance(); UpdateSessionState(); ResetFileSource(); RenderNavigation(); prompt.Focus(); _ = RefreshActivityAsync(); _ = RefreshProjectKnowledgeAsync();
        _ = RefreshMainChatWorkflowAvailabilityAsync();
    }
    private async Task SelectSession(string id, CancellationToken cancellationToken = default,
        bool recordHistory = true, bool? historyLegacyRoute = null)
    {
        RequireSemanticAttachCurrent();
        EnsureAttachmentNavigationAllowed(); EnsureConversationControlsSettled();
        if (NativeMutationPending) throw new InvalidOperationException("Wait for this request's acceptance before selecting a conversation.");
        if (NativeMutationUncertain && CurrentConversationId != id) EnsureNativeNavigationAllowed();
        if (recordHistory) RememberCurrentConversationVisit();
        var selectionVersion = ++conversationSelectionVersion;
        AdvanceSemanticAttachNavigation(selectionVersion);
        if (durableConversations.TryGetValue(id, out var durable))
        {
            await OpenDurableConversationAsync(durable, selectionVersion, cancellationToken);
            if (recordHistory && selectionVersion == conversationSelectionVersion && CurrentConversationId == id) RememberCurrentConversationVisit();
            return;
        }
        if (!(historyLegacyRoute ?? showingLegacyNavigation) && !activeJobs.Any(x => x.SessionId == id))
        {
            // Saved drafts and older sidebar targets can be outside the bounded recent page.
            // Resolve a single owner-root exact identity before the ordinary Chat-only lookup.
            var exact = await FindExactWorkflowConversationAsync(id);
            cancellationToken.ThrowIfCancellationRequested();
            if (selectionVersion != conversationSelectionVersion) return;
            RequireSemanticAttachCurrent();
            if (exact is not null)
            {
                exactWorkflowConversations[id] = exact;
                durableConversations[id] = exact.Summary;
                await OpenDurableConversationAsync(exact.Summary, selectionVersion, cancellationToken);
                if (recordHistory && selectionVersion == conversationSelectionVersion && CurrentConversationId == id) RememberCurrentConversationVisit();
                return;
            }
            var selected = await Client.GetAsync<ConversationSummary>("conversations.get", new { id }, cancellationToken: cancellationToken);
            if (selectionVersion != conversationSelectionVersion) return;
            RequireSemanticAttachCurrent();
            if (selected.Id != id) throw new InvalidDataException("The service returned a different conversation identity.");
            durableConversations[id] = selected; await OpenDurableConversationAsync(selected, selectionVersion, cancellationToken);
            if (recordHistory && selectionVersion == conversationSelectionVersion && CurrentConversationId == id) RememberCurrentConversationVisit();
            return;
        }
        if (NativeMutationUncertain) throw new InvalidOperationException("Refresh or retry the unconfirmed request before selecting another conversation.");
        var changedSession = session?.Id != id;
        if (session is not null && session.Id != id) RememberComposerDraft(session.Id, prompt.Text);
        using var selectionLifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, Lifetime.Token);
        var loaded = await Client.GetAsync<TaskSession>("sessions.get", new { id }, cancellationToken: selectionLifetime.Token);
        cancellationToken.ThrowIfCancellationRequested();
        if (selectionVersion != conversationSelectionVersion) return;
        RequireSemanticAttachCurrent();
        EnsureNativeNavigationAllowed();
        if (!IsUserConversation(loaded)) throw new InvalidOperationException("This is an internal agent session. Its progress and results are available in All work; user conversations stay with Cerebral.");
        ResetTeamForChangedMain();
        durableConversationId = null; nativeChat?.Reset(); if (nativeChat is not null) nativeChat.Visibility = Visibility.Collapsed; transcript.Visibility = Visibility.Visible;
        session = loaded; if (changedSession) ResetDesktopConversation(); pendingSubmission = null; loadedQuestions.Clear(); ResetQuestionDesktopTextEntries(); questionPanel.Children.Clear(); loadedQuestionCount = 0; UpdateQuestionHostVisibility();
        RefreshConversationTokenBalance(loadSessionPreference: true);
        selectedNavigationId = navigation.Nodes.FirstOrDefault(x => x.Kind == "conversation" && x.TargetId == id)?.Id ?? "conversation-" + id;
        RestoreComposerDraftText(id);
        sessionHeading.Text = "Previous · " + loaded.Title; UpdateAgentSummary(); RenderNavigation(); await RefreshProjectKnowledgeAsync();
        if (selectionVersion != conversationSelectionVersion || session?.Id != id) return;
        if (changedSession) { transcriptRestorePending = true; transcriptFollowEnd = true; transcriptSavedOffset = 0; }
        liveJobId = null; liveOutput.Clear(); liveDetails.Visibility = Visibility.Collapsed; liveDetails.IsExpanded = false; liveLabel.Text = "";
        RenderSession(); await RefreshTurnActivityAsync(); await RefreshQuestions(); await RefreshInterfaceRequests(); await RefreshActivityAsync(); UpdateSessionState();
        if (recordHistory && selectionVersion == conversationSelectionVersion && CurrentConversationId == id) RememberCurrentConversationVisit();
    }
    private void RenderWelcome()
    {
        var welcome = new StackPanel { Margin = new Thickness(22, 30, 22, 22) };
        welcome.Children.Add(Ui.Text("What would you like to work on?", 25));
        welcome.Children.Add(Ui.Text("Describe the result you want. Cerebral can work with project files, use development tools, and delegate focused work to its configured helpers.", 15, Ui.Muted));
        welcome.Children.Add(Ui.Text("Each conversation keeps its own working context. You can leave this window and return to work that continues in the service.", 13, Ui.Muted));
        foreach (var contribution in desktopPlugins?.NewConversationViews ?? [])
        {
            if (contribution.View.Parent is Panel oldPanel) oldPanel.Children.Remove(contribution.View);
            else if (contribution.View.Parent is ContentControl oldContent) oldContent.Content = null;
            else if (contribution.View.Parent is Decorator oldDecorator) oldDecorator.Child = null;
            var frame = new Border
            {
                Child = contribution.View,
                Margin = new Thickness(0, 18, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Background = Ui.RaisedSurface,
                BorderBrush = Ui.Line,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(16)
            };
            AutomationProperties.SetAutomationId(frame, "PluginNewConversationStart-" + contribution.PluginId + "-" + contribution.Id);
            AutomationProperties.SetName(frame, contribution.Title + " new conversation plugin");
            welcome.Children.Add(frame);
        }
        messages.Children.Clear(); messages.Children.Add(welcome);
        ResetQuestionDesktopTextEntries(); questionPanel.Children.Clear(); loadedQuestionCount = 0; UpdateQuestionHostVisibility();
    }
    private void RenderSession()
    {
        CaptureTranscriptPosition();
        messages.Children.Clear(); if (session is null) { transcriptRestorePending = false; return; }
        var renderedSessionId = session.Id;
        var historyByTurn = new Dictionary<int, List<SessionMessage>>(); var historyTurn = 0;
        foreach (var entry in session.Messages)
        {
            if (entry.Role == "user" && entry.Origin == "user" && entry.ToolName is null) historyTurn = entry.Sequence;
            if (entry.ToolName is not null || entry.Origin is "tool" or "helper" or "service" || entry.Role == "tool" || TryActivityAction(entry.Content, out _) || SavedDelegationNotice(entry))
            {
                if (!historyByTurn.TryGetValue(historyTurn, out var bucket)) historyByTurn[historyTurn] = bucket = [];
                bucket.Add(entry);
            }
        }
        if (session.HasEarlierMessages && session.OldestLoadedSequence is not null)
        {
            var shown = session.Messages.Count;
            var total = Math.Max(session.MessageCount, shown);
            var loadEarlier = Ui.Button($"Load earlier messages · {shown:N0} of {total:N0} shown",
                () => Run(() => LoadEarlierMessages(renderedSessionId)));
            loadEarlier.HorizontalAlignment = HorizontalAlignment.Center;
            loadEarlier.Margin = new Thickness(0, 8, 0, 12);
            AutomationProperties.SetAutomationId(loadEarlier, "LoadEarlierMessages");
            messages.Children.Add(loadEarlier);
        }
        if (!string.IsNullOrEmpty(session.Checkpoint))
            messages.Children.Add(new Expander { Header = $"Context checkpoint · through message {session.CheckpointThroughSequence}", Content = MarkdownView.Render(session.Checkpoint), Margin = new Thickness(0, 8, 0, 12) });
        DateOnly? renderedDay = null;
        foreach (var item in session.Messages)
        {
            if (item.ExcludedFromContext)
            {
                AppendTranscriptDay(item.At, ref renderedDay);
                messages.Children.Add(new Border { Padding = new Thickness(13, 7, 13, 7), Margin = new Thickness(0, 0, 0, 8), BorderBrush = Ui.Line, BorderThickness = new Thickness(0, 0, 0, 1),
                    Child = Ui.Text($"Message {item.Sequence} excluded from model context", 12, Ui.Muted) });
                if (ActualUserTurn(item)) messages.Children.Add(InlineTurnActivity(item.Sequence, historyByTurn.GetValueOrDefault(item.Sequence)));
                continue;
            }
            var isUser = item.Role == "user" && item.Origin == "user" && item.ToolName is null;
            var isTool = item.ToolName is not null || item.Origin == "tool" || item.Role == "tool";
            var isHelper = item.Origin == "helper";
            var isService = item.Origin == "service";
            var isQuestionAnswer = item.Origin == "question-answer" && item.SourceQuestionId is not null;
            var toolCall = ToolCallName(item);
            if (isTool || isHelper || isService || toolCall is not null || TryActivityAction(item.Content, out _) || SavedDelegationNotice(item)) continue;
            if (item.Role == "assistant" && loadedQuestions.Any(x => item.Content == "Question: " + x.Prompt)) continue;
            AppendTranscriptDay(item.At, ref renderedDay);
            var answerQuestion = isQuestionAnswer ? loadedQuestions.FirstOrDefault(x => x.Id == item.SourceQuestionId) : null;
            var speaker = isQuestionAnswer
                ? answerQuestion?.SessionId is { } questionSessionId && questionSessionId != renderedSessionId ? "YOUR ANSWER · HELPER QUESTION" : "YOUR ANSWER"
                : isTool ? "TOOL" + (item.ToolName is null ? "" : " · " + item.ToolName) : isHelper ? "HELPER RESULT" :
                item.Origin == "service" ? "SERVICE UPDATE" : toolCall is not null ? "TOOL CALL · " + toolCall :
                item.Role == "assistant" ? "CEREBRAL" : isUser ? "YOU" : item.Role.ToUpperInvariant();
            var body = new StackPanel();
            var speakerLabel = Ui.Text(speaker, 11, Ui.Muted);
            AutomationProperties.SetAutomationId(speakerLabel, "MessageSpeaker-" + item.Sequence);
            body.Children.Add(speakerLabel);
            var text = item.Content.Length > 160000 ? item.Content[..160000] + "\n[Long message display limited. Use session details for the retained record.]" : item.Content;
            var rendered = MarkdownView.Render(text, (code, language) => Run(() => SaveCode(code, language)), ex => SetStatus(ex, true), path => Run(async () =>
            {
                sharedFiles.IsChecked = false; filePath.Text = path; SelectPage("files"); await ReadFile();
            }));
            AutomationProperties.SetAutomationId(rendered, "MessageBody-" + item.Sequence);
            if (rendered is RichTextBox selectableMessage)
                TextContextMenus.Attach(selectableMessage, menu => AppendMessageContextActions(menu, item, isUser, renderedSessionId));
            if (isTool || isHelper || isService || toolCall is not null)
                body.Children.Add(new Expander { Header = isTool ? "View tool result" : isHelper ? "View helper result" : isService ? "View service update" : "View tool arguments", Content = rendered });
            else AddDesktopMessagePresentation(body, rendered, item);
            PresentDesktopMessage(item);
            var copy = Ui.Button("Copy", () => Run(() =>
            {
                Clipboard.SetText(item.Content); SetStatus("Message copied."); return Task.CompletedTask;
            }));
            copy.FontSize = 12; copy.ToolTip = "Copy the complete message, including Markdown and code.";
            AutomationProperties.SetAutomationId(copy, "CopyMessage-" + item.Sequence);
            AutomationProperties.SetName(copy, "Copy " + (isUser ? "your message" : isTool ? "tool result" : isHelper ? "helper result" : "Cerebral message"));
            var actions = Ui.Bar(copy); actions.Margin = new Thickness(0);
            if (isUser)
            {
                var remove = Ui.Button("Remove from context", () => Run(() => ExcludeMessage(renderedSessionId, item.Sequence)));
                remove.FontSize = 12;
                AutomationProperties.SetAutomationId(remove, "RemoveMessage-" + item.Sequence);
                actions.Children.Add(remove);
            }
            var footer = new Grid { Margin = new Thickness(0, 9, 0, 0) };
            footer.ColumnDefinitions.Add(new ColumnDefinition()); footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            footer.Children.Add(actions);
            var timestamp = Ui.Text(MessageTimeFormatPolicy.Format(item.At, messageTimeFormat), 11, Ui.Muted);
            timestamp.HorizontalAlignment = HorizontalAlignment.Right; timestamp.VerticalAlignment = VerticalAlignment.Bottom; timestamp.Margin = new Thickness(14, 6, 0, 0);
            AutomationProperties.SetAutomationId(timestamp, "MessageTime-" + item.Sequence);
            Grid.SetColumn(timestamp, 1); footer.Children.Add(timestamp); body.Children.Add(footer);
            var messageBorder = new Border { Child = body, Background = isUser || isQuestionAnswer ? Ui.Paper : Ui.Surface, BorderBrush = Ui.Line, BorderThickness = new Thickness(1), Padding = new Thickness(14), Margin = new Thickness(0, 0, 0, 10), CornerRadius = new CornerRadius(7) };
            AutomationProperties.SetAutomationId(messageBorder, "Message-" + item.Sequence);
            if (isQuestionAnswer) AutomationProperties.SetAutomationId(messageBorder, "QuestionAnswer-" + item.SourceQuestionId);
            messages.Children.Add(messageBorder);
            if (isUser) messages.Children.Add(InlineTurnActivity(item.Sequence, historyByTurn.GetValueOrDefault(item.Sequence)));
        }
        pluginMessageBaseline = true;
        RestoreTranscriptPosition(renderedSessionId);
    }
    private void AppendTranscriptDay(DateTimeOffset at, ref DateOnly? renderedDay)
    {
        var local = at.ToLocalTime();
        var day = DateOnly.FromDateTime(local.DateTime);
        if (renderedDay == day) return;
        renderedDay = day;
        var label = day == DateOnly.FromDateTime(DateTime.Now) ? "Today · " + local.ToString("dddd, MMMM d") : local.ToString("dddd, MMMM d, yyyy");
        var line = new Border { Height = 1, Background = Ui.Line, VerticalAlignment = VerticalAlignment.Center };
        var text = Ui.Text(label, 12, Ui.Ink); text.Margin = new Thickness(0);
        var badge = new Border { Child = text, Background = Ui.RaisedSurface, BorderBrush = Ui.Line, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10), Padding = new Thickness(12, 4, 12, 4), HorizontalAlignment = HorizontalAlignment.Center };
        var separator = new Grid { Margin = new Thickness(0, 9, 0, 13) };
        separator.Children.Add(line); separator.Children.Add(badge);
        AutomationProperties.SetAutomationId(separator, "TranscriptDay-" + day.ToString("yyyy-MM-dd"));
        messages.Children.Add(separator);
    }
    private void AppendMessageContextActions(ContextMenu menu, SessionMessage item, bool isUser, string sessionId)
    {
        menu.Items.Add(new Separator());
        var copy = new MenuItem { Header = "Copy complete _message" };
        AutomationProperties.SetAutomationId(copy, "CopyMessageContext-" + item.Sequence);
        copy.Click += (_, _) =>
        {
            try { Clipboard.SetText(item.Content); SetStatus("Message copied."); }
            catch (Exception ex) { SetStatus(ex.Message, true); }
        };
        menu.Items.Add(copy);
        if (!isUser) return;
        var remove = new MenuItem { Header = "_Remove from model context" };
        AutomationProperties.SetAutomationId(remove, "RemoveMessageContext-" + item.Sequence);
        remove.Click += (_, _) => Run(() => ExcludeMessage(sessionId, item.Sequence));
        menu.Items.Add(remove);
    }
    private async Task LoadEarlierMessages(string sessionId)
    {
        var current = session;
        if (current?.Id != sessionId || !current.HasEarlierMessages || current.OldestLoadedSequence is not { } before) return;
        var older = await Get<TaskSession>("sessions.get", new { id = sessionId, beforeSequence = before, messageLimit = 200 });
        if (session?.Id != sessionId) return;
        var combined = older.Messages.Concat(current.Messages).GroupBy(x => x.Sequence)
            .Select(x => x.First()).OrderBy(x => x.Sequence).ToList();
        current.Messages = combined;
        current.MessageCount = Math.Max(current.MessageCount, older.MessageCount);
        current.OldestLoadedSequence = combined.FirstOrDefault()?.Sequence;
        current.HasEarlierMessages = older.HasEarlierMessages;
        session = current;
        RenderSession();
        SetStatus(current.HasEarlierMessages
            ? $"Loaded earlier conversation history. {combined.Count:N0} of {current.MessageCount:N0} messages are shown."
            : $"Loaded the retained conversation history ({combined.Count:N0} messages)."
        );
    }
    private bool SavedDelegationNotice(SessionMessage message) => message.Role == "assistant" &&
        activeJobs.Any(job => job.ParentJobId is not null && message.Content == $"Delegated assignment {job.Id} to {job.ProfileId}");
    private string? ToolCallName(SessionMessage item)
    {
        if (item.Role != "assistant" || session is null) return null;
        var result = session.Messages.FirstOrDefault(x => x.Sequence == item.Sequence + 1);
        if (result?.ToolName is null) return null;
        try
        {
            using var document = JsonDocument.Parse(item.Content);
            var root = document.RootElement;
            return root.ValueKind == JsonValueKind.Object && root.TryGetProperty("action", out var action) && action.ValueKind == JsonValueKind.String && action.GetString() == "tool" &&
                root.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String && name.GetString() == result.ToolName ? result.ToolName : null;
        }
        catch (JsonException) { return null; }
    }
    private async Task ExcludeMessage(string sessionId, int sequence)
    {
        var dialog = new FieldsDialog(this, "Remove from model context",
            "The selected message will be excluded from future model requests. Stored history is retained; files and tool actions already produced are unchanged. Finish or cancel active work in this conversation first.", "Remove from context")
            { Height = 330, Width = 600 };
        dialog.Check("following", "Also exclude following messages and replies", true);
        if (dialog.ShowDialog() != true) return;
        var updated = await Get<TaskSession>("sessions.exclude", new { id = sessionId, sequence, includeFollowing = dialog.Checked("following") });
        if (session?.Id == sessionId) { session = updated; RenderSession(); UpdateSessionState(); }
        await RefreshSessions(); SetStatus("Message removed from future model context. Existing files and completed actions remain unchanged.");
    }
    private async Task Send()
    {
        var priorTiming = mainChatTimingProbe;
        TryArmMainChatTiming();
        var attemptedTiming = ReferenceEquals(mainChatTimingProbe, priorTiming) ? null : mainChatTimingProbe;
        try
        {
            var review = await ReviewNormalExactFollowupAsync(Lifetime.Token);
            if (review?.Cancelled == true)
            {
                if (attemptedTiming is not null && ReferenceEquals(mainChatTimingProbe, attemptedTiming))
                    CloseMainChatTiming(MainChatTimingEnd.NoSubmission);
                SetStatus("Workflow input review cancelled. Your message draft is unchanged.");
                return;
            }
            var previousInput = mainDefinitionInput.Value;
            MainWorkflowDefinitionInputLease? inputLease = null;
            string? requestId = null;
            try
            {
                if (review?.Input is { } reviewedInput)
                {
                    if (previousInput is not null)
                        throw new InvalidOperationException("A workflow input request cannot nest another send.");
                    requestId = Guid.NewGuid().ToString("N");
                    inputLease = CaptureMainDefinitionInput(requestId, reviewedInput, exact: true);
                    mainDefinitionInput.Value = inputLease;
                }
                var completionScope = CaptureCompletionScope();
                var result = await SubmitComposerAsync(requestId);
                PresentNativeSubmissionResult(completionScope, result);
                if (attemptedTiming is not null && ReferenceEquals(mainChatTimingProbe, attemptedTiming) &&
                    (result.State != DesktopOperationState.Accepted || !attemptedTiming.IsBound))
                    CloseMainChatTiming(result.State switch
                    {
                        DesktopOperationState.Uncertain => MainChatTimingEnd.Uncertain,
                        DesktopOperationState.Rejected => MainChatTimingEnd.Rejected,
                        DesktopOperationState.Failed => MainChatTimingEnd.Failed,
                        _ => MainChatTimingEnd.NoSubmission
                    });
            }
            finally
            {
                inputLease?.Dispose();
                mainDefinitionInput.Value = previousInput;
            }
        }
        catch
        {
            if (attemptedTiming is not null && ReferenceEquals(mainChatTimingProbe, attemptedTiming))
                CloseMainChatTiming(MainChatTimingEnd.SendFailed);
            throw;
        }
    }
    private async Task<DesktopOperationResult> SubmitComposerAsync(string? requestId = null)
    {
        RequireSemanticAttachCurrent();
        EnsureSemanticAttachSubmissionAllowed();
        if (session is not null) throw new InvalidOperationException("This is a previous-generation conversation. Use New chat to continue with the durable conversation engine.");
        if (retainedNativeSubmission is { } retained)
        {
            if (requestId != retained.RequestId)
                return new(DesktopOperationState.Rejected, "Use Retry request to confirm the retained message before sending another.", retained.RequestId);
            CheckRetainedMainDefinitionInput(retained);
            return await SubmitNativeDesktopInput(new(PluginBinding(), retained.RequestId, retained.UserText),
                retained.AssignmentText, Lifetime.Token);
        }
        var workflowIntent = CaptureMainChatWorkflowIntent();
        if (workflowIntent is { } exactIntent && exactIntent.DefinitionId != ConversationControlClient.ChatDefinitionId &&
            (mainDefinitionInput.Value is null || exactIntent.ExactAvailability is null || !MainExactWorkflowAvailabilityCurrent()))
            throw new InvalidOperationException("The selected exact workflow needs its complete input and a current service declaration before sending.");
        var previousWorkflowIntent = mainChatWorkflowIntent.Value;
        mainChatWorkflowIntent.Value = workflowIntent;
        try
        {
            var commandRequestId = requestId ?? Guid.NewGuid().ToString("N");
            RequireMainDefinitionInputCurrent(commandRequestId);
            var preparation = await PrepareComposerTextEntrySubmissionAsync(commandRequestId, Lifetime.Token);
            RequireSemanticAttachCurrent();
            EnsureSemanticAttachSubmissionAllowed();
            RequireMainChatWorkflowIntentCurrent(workflowIntent);
            if (preparation is { } blocked) return blocked;
            var profile = ConversationProfile(); var text = prompt.Text;
            if (string.IsNullOrWhiteSpace(text) && attachmentDrafts.Count == 0) throw new ArgumentException("Enter a message or attach a file.");
            await ReconcileInterruptedNativeWorkAsync(Lifetime.Token);
            RequireSemanticAttachCurrent();
            EnsureSemanticAttachSubmissionAllowed();
            RequireMainChatWorkflowIntentCurrent(workflowIntent);
            if (NativeWorkActive && !NativeMutationUncertain) throw new InvalidOperationException("This conversation already has active work. Answer its questions, stop it, or wait before continuing.");
            var command = string.IsNullOrWhiteSpace(text)
                ? new DesktopComposerCommandApplication(text, false, null)
                : await ApplyDesktopComposerCommandsAsync(new(PluginBinding(), commandRequestId, text), false, Lifetime.Token);
            RequireSemanticAttachCurrent();
            EnsureSemanticAttachSubmissionAllowed();
            RequireMainChatWorkflowIntentCurrent(workflowIntent);
            if (command.Handled)
            {
                if (prompt.Text == text) prompt.Clear();
                return DefinitionInputComposerOutcome(new(DesktopOperationState.Completed, command.Message ?? "The conversation command was applied.", commandRequestId));
            }
            if (command.Text != text)
            {
                text = command.Text;
                if (prompt.Text != text) prompt.Text = text;
                if (string.IsNullOrWhiteSpace(text) && attachmentDrafts.Count == 0)
                {
                    prompt.Clear();
                    return DefinitionInputComposerOutcome(new(DesktopOperationState.Completed, command.Message ?? "The conversation command was applied.", commandRequestId));
                }
            }
            if (pendingSubmission is null || pendingSubmission.Prompt != text || pendingSubmission.ProfileId != profile.Id || pendingSubmission.SessionId != CurrentConversationId || pendingSubmission.ProjectId != ConversationProjectId || pendingSubmission.ResponseSharePercent != selectedResponseSharePercent || requestId is not null && pendingSubmission.RequestId != requestId)
                pendingSubmission = new SubmitJobRequest { RequestId = commandRequestId, SessionId = CurrentConversationId, ProjectId = ConversationProjectId, ProfileId = profile.Id, Prompt = text, Title = durableConversationTitle, ResponseSharePercent = selectedResponseSharePercent };
            var input = new DesktopSubmission(PluginBinding(), pendingSubmission.RequestId!, text);
            FreezeAttachments(input, profile.Id);
            RequireSemanticAttachCurrent();
            EnsureSemanticAttachSubmissionAllowed();
            RequireMainChatWorkflowIntentCurrent(workflowIntent);
            attachmentInputInterception = true; RenderAttachmentDrafts();
            try
            {
                var interception = await InterceptDesktopInputAsync(input, false, Lifetime.Token);
                RequireSemanticAttachCurrent();
                EnsureSemanticAttachSubmissionAllowed();
                RequireMainChatWorkflowIntentCurrent(workflowIntent);
                if (interception is { } held) return DefinitionInputComposerOutcome(held);
            }
            finally { attachmentInputInterception = false; RenderAttachmentDrafts(); }
            RequireSemanticAttachCurrent();
            EnsureSemanticAttachSubmissionAllowed();
            RequireMainChatWorkflowIntentCurrent(workflowIntent);
            SetStatus("Submitting assignment…");
            var result = await desktopPluginHost!.SubmitAsync(input, Lifetime.Token);
            return DefinitionInputComposerOutcome(result);
        }
        finally { mainChatWorkflowIntent.Value = previousWorkflowIntent; }
    }
    private async Task Steer()
    {
        if (ControlUncertain)
        {
            if (conversationControl?.Snapshot.Steering is null) throw new InvalidOperationException("Use Retry pending action to resolve the retained conversation action first.");
            await RetryConversationControlAsync(); return;
        }
        if (NativeMutationPending) throw new InvalidOperationException("Wait for the pending request before steering.");
        var currentId = IsNativeChat ? nativeChat?.Snapshot?.Run is { IsTerminal: false, CancelRequested: false } run ? run.Id :
            throw new InvalidOperationException("There is no active conversation run to steer.") :
            (CurrentJob() ?? throw new InvalidOperationException("There is no active job in this conversation.")).Id;
        if (string.IsNullOrWhiteSpace(prompt.Text)) throw new ArgumentException("Enter steering information in the message box.");
        var text = prompt.Text;
        if (pendingSteering is not { } pending || pending.JobId != currentId || pending.Text != text) pendingSteering = (currentId, text, Guid.NewGuid().ToString("N"));
        var input = new DesktopSubmission(PluginBinding(), pendingSteering.Value.RequestId, text);
        if (await InterceptDesktopInputAsync(input, true, Lifetime.Token) is { } held)
        {
            SetStatus(held.Message ?? held.State.ToString(), held.State is DesktopOperationState.Rejected or DesktopOperationState.Failed or DesktopOperationState.Uncertain);
            return;
        }
        var result = await desktopPluginHost!.SteerAsync(input, Lifetime.Token);
        SetStatus(result.Message ?? result.State.ToString(), result.State is DesktopOperationState.Rejected or DesktopOperationState.Failed or DesktopOperationState.Uncertain);
    }
    private async Task CompactSession()
    {
        if (IsNativeChat) { await RequestDurableConversationCompactionAsync(); return; }
        if (session is null) throw new InvalidOperationException("Open a conversation first.");
        var result = await Command("sessions.compact", new { id = session.Id }); await RefreshJobs(); SetStatus("Compaction requested. Its result will appear in this conversation.");
    }
    private async Task CancelCurrentJob() { var current = CurrentJob() ?? throw new InvalidOperationException("There is no active job to cancel."); await Command("jobs.cancel", new { id = current.Id }); await RefreshJobs(); SetStatus("Cancellation requested."); }
    private async Task ReconcileCurrentJob()
    {
        if (IsNativeChat) { await nativeChat!.RefreshAsync(); SetStatus("Saved work refreshed. Use Check outcome for an uncertain tool result or Retry request for an unconfirmed submission."); return; }
        var current = CurrentJob() ?? throw new InvalidOperationException("There is no active job in this conversation.");
        await Command("jobs.reconcile", new { id = current.Id }); await RefreshJobs(); SetStatus("Remote connection retry requested.");
    }

    protected override async Task OnEventAsync(ServiceEvent item)
    {
        try { mainChatTimingProbe?.ObservePreviewEnvelope(item); } catch { /* Diagnostic-only. */ }
        await NotifySystemInferenceOwnershipAsync(item);
        workflowWorkspace?.Notify(item);
        nativeChat?.Notify(item);
        if (teamSelectedRunId is { } monitored && teamPresenters.TryGetValue(monitored, out var teamPresenter))
            teamPresenter.Notify(item);
        if (item.Type == "conversation.workspace.updated") await RefreshConversationWorkspaceAsync();
        if (item.Type == "conversation.updated" && item.JobId is { } renamedConversation)
            await RefreshConversationTitleAsync(renamedConversation);
        if (item.Type == "workflow.updated" && !showingLegacyNavigation && !readingOlderChats) await RefreshDurableConversationsAsync();
        if (item.Type == "workflow.updated" &&
            (TeamRun(item.JobId) is not null || item.JobId == teamSelectedRunId)) QueueTeamRefresh();
        if (item.Type == "collaboration.updated" && IsNativeChat) await RefreshTeamForCurrentMainAsync();
        if (item.Type == "collaboration.permission.updated") await RefreshInterfaceRequests();
        await NativePluginManagementEventAsync(item);
        await PluginControlEventAsync(item);
        await InterfaceEventAsync(item);
        await ActivityEventAsync(item);
        await TurnActivityEventAsync(item);
        if (item.Type == "project.knowledge.updated")
        {
            if (item.Data.TryGetProperty("projectId", out var project) && project.GetString() is { } projectId) projectKnowledge.Remove(projectId);
            else projectKnowledge.Clear();
            await RefreshProjectKnowledgeAsync();
        }
        if (item.Type == "navigation.updated") await RefreshSessions();
        if (item.Type == "settings.updated")
        {
            await HydratePortableInterfacePreferencesAsync();
            await RefreshRemoteSlotRegistrationsAsync();
            await RefreshProfileRoutesAfterSettingsAsync();
        }
        if (item.Type == "agents.updated")
        {
            await RefreshProfiles();
            await RefreshFreshNormalChatWorkflowAfterProfilesAsync(Lifetime.Token);
        }
        if (item.Type == "model.updated")
        {
            configuredModels = await Get<List<ModelRecord>>("models.list"); UpdateAgentSummary();
        }
        if (item.Type == "token" && item.Data.TryGetProperty("sessionId", out var sid) && sid.GetString() == session?.Id && item.Data.TryGetProperty("text", out var text))
        {
            liveJobId = item.JobId;
            liveLabel.Text = "Cerebral is working…"; liveDetails.Visibility = Visibility.Collapsed;
            // Streaming action JSON is not a user response or a record of completed work.
            liveOutput.Clear();
        }
        if (item.Type == "job.updated")
        {
            await RefreshJobs(); var job = activeJobs.FirstOrDefault(x => x.Id == item.JobId);
            if (job is not null && session is not null && job.SessionId == session.Id && !Active(job))
            {
                var id = session!.Id; var fresh = await Get<TaskSession>("sessions.get", new { id });
                if (session?.Id == id) { session = fresh; RenderSession(); liveOutput.Clear(); liveDetails.Visibility = Visibility.Collapsed; liveDetails.IsExpanded = false; liveLabel.Text = ""; await RefreshQuestions(); }
            }
            await RefreshSessions();
        }
        if (item.Type == "session.updated" && item.Data.TryGetProperty("id", out var updatedId) && updatedId.GetString() == session?.Id)
        {
            var id = session!.Id; var fresh = await Get<TaskSession>("sessions.get", new { id }); if (session?.Id == id) { session = fresh; RenderSession(); await RefreshTurnActivityAsync(); await RefreshProjectKnowledgeAsync(); }
        }
        if (item.Type == "question.updated")
        {
            // A helper may ask before its job event reaches this client. Refresh ancestry first so its internal
            // session is associated with the open lead conversation on this same event.
            await RefreshJobs(); await RefreshQuestions();
        }
    }
    protected override Task RefreshAfterReconnectAsync()
    {
        workflowWorkspace?.ResetGenerationPreviewFeed();
        nativeChat?.ResetGenerationPreviewFeed();
        projectKnowledge.Clear();
        return Task.CompletedTask;
    }
    private async Task RefreshQuestions()
    {
        if (session is null) { loadedQuestionCount = 0; questionPanel.Children.Clear(); UpdateQuestionHostVisibility(); return; }
        var id = session.Id;
        var relatedJobs = activeJobs.Where(x => x.SessionId == id).Select(x => x.Id).ToHashSet(StringComparer.Ordinal);
        bool added;
        do
        {
            added = false;
            foreach (var job in activeJobs)
                if (job.ParentJobId is { } parentId && relatedJobs.Contains(parentId)) added |= relatedJobs.Add(job.Id);
        } while (added);
        var relatedSessions = activeJobs.Where(x => relatedJobs.Contains(x.Id)).Select(x => x.SessionId).Append(id).Distinct(StringComparer.Ordinal);
        var questionGroups = await Task.WhenAll(relatedSessions.Select(relatedId => Get<List<UserQuestion>>("questions.list", new { sessionId = relatedId })));
        if (session?.Id != id) return;
        var questions = questionGroups.SelectMany(x => x).DistinctBy(x => x.Id).ToList();
        RenderQuestions(id, questions);
    }
    private IReadOnlyList<UserQuestion> QuestionsRequiringInput(IEnumerable<UserQuestion> questions) =>
        questions.Where(x => x.Answer is null && !x.Assumed &&
                (!x.Required || activeJobs.Any(job => job.Id == x.JobId && Active(job))))
            .OrderBy(x => x.CreatedAt).ToList();
    private void RenderQuestions(string id, List<UserQuestion> questions)
    {
        var pendingQuestions = QuestionsRequiringInput(questions);
        var nextFocusKey = pendingQuestions.Count == 0 ? null : id + ":" + string.Join(",", pendingQuestions.Select(item => item.Id));
        if (nextFocusKey != questionFocusKey) { questionFocusKey = nextFocusKey; questionFocusPending = nextFocusKey is not null; }
        loadedQuestionCount = pendingQuestions.Count; loadedQuestions = questions;
        ResetQuestionDesktopTextEntries(); questionPanel.Children.Clear();
        foreach (var question in pendingQuestions)
        {
            var card = new StackPanel(); card.Children.Add(Ui.Text(QuestionHeading(question), 11, question.Required && question.Answer is null ? Ui.ErrorBorder : Ui.Accent));
            if (question.SessionId != id)
            {
                var helper = activeJobs.FirstOrDefault(x => x.Id == question.JobId);
                card.Children.Add(Ui.Text("Delegated work · " + (profiles.FirstOrDefault(x => x.Id == helper?.ProfileId)?.Name ?? helper?.ProfileId ?? "helper"), 11, Ui.Muted));
            }
            card.Children.Add(Ui.Text(question.Prompt, 14)); AddQuestionReason(card, question);
            if (question.DeadlineAt is { } deadline && question.Answer is null && !question.Assumed)
                card.Children.Add(Ui.Text(question.CanAssume ? $"If unanswered by {deadline.LocalDateTime:t}, work may continue using: {question.Assumption}" : $"Answer requested by {deadline.LocalDateTime:t}; this question requires your input.", 12, Ui.Muted));
            if (!question.Assumed)
            {
                var answer = Ui.Input(answerDrafts.GetValueOrDefault(question.Id, ""), true); answer.MinHeight = 65; answer.MaxHeight = 150;
                TextContextMenus.EnableWritingAssistance(answer);
                AutomationProperties.SetAutomationId(answer, "QuestionAnswer-" + question.Id);
                answer.TextChanged += (_, _) => answerDrafts[question.Id] = answer.Text;
                if (question.Choices.Count > 0)
                {
                    card.Children.Add(Ui.Text(question.KnowledgeProposalId is null ? "Choose an option or write your own answer." : "Choose approve or reject, or write what should change.", 12, Ui.Muted));
                    var choices = new ComboBox { ItemsSource = question.Choices, SelectedItem = question.Choices.Contains(answer.Text) ? answer.Text : null, Margin = new Thickness(0, 0, 0, 9) };
                    AutomationProperties.SetAutomationId(choices, "QuestionChoices-" + question.Id);
                    choices.SelectionChanged += (_, _) => { if (choices.SelectedItem is string choice) answer.Text = choice; }; card.Children.Add(choices);
                }
                card.Children.Add(answer);
                var textEntryActions = RegisterDesktopTextEntry(answer, "question-" + question.Id, "Answer to: " + question.Prompt, true);
                card.Children.Add(textEntryActions);
                var submit = Ui.Button("Submit answer", () => Run(() => SubmitQuestionAnswer(question.Id, answer)), true);
                AutomationProperties.SetAutomationId(submit, "SubmitAnswer-" + question.Id);
                submit.Margin = new Thickness(0, 8, 0, 0); card.Children.Add(submit);
            }
            var border = new Border { Child = card, Padding = new Thickness(11), Margin = new Thickness(0, 0, 0, 12), BorderBrush = Ui.Line, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4), Background = Ui.Paper };
            AutomationProperties.SetAutomationId(border, "QuestionCard-" + question.Id);
            questionPanel.Children.Add(border);
        }
        UpdateQuestionHostVisibility();
        RenderSession();
    }
    private async Task SubmitQuestionAnswer(string questionId, TextBox answer)
    {
        if (string.IsNullOrWhiteSpace(answer.Text)) throw new ArgumentException("Enter an answer or select a choice.");
        var selectedSessionId = session?.Id ?? throw new InvalidOperationException("Open the conversation that owns this question.");
        questionPanel.IsEnabled = false;
        try
        {
            await Command("questions.answer", new { id = questionId, answer = answer.Text });
            answerDrafts.Remove(questionId);
            var fresh = await Get<TaskSession>("sessions.get", new { id = selectedSessionId });
            if (session?.Id == selectedSessionId) session = fresh;
            await RefreshJobs();
            await RefreshQuestions();
            SetStatus("Answer saved.");
        }
        finally { questionPanel.IsEnabled = true; }
    }
    private void UpdateQuestionHostVisibility()
    {
        if (questionHost is null) return;
        questionHost.Visibility = loadedQuestionCount > 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateComposerQuestionSurface();
    }

    private async Task<ToolResult> CallTool(AgentProfile profile, string name, object arguments, CerebralClient? client = null)
    {
        // A transfer to another worker has no coordinator session binding; local tools use this conversation.
        var result = client is null ? await Get<ToolResult>("tools.call", new { profileId = profile.Id, sessionId = session?.Id, name, arguments }) :
            await client.GetAsync<ToolResult>("tools.call", new { profileId = profile.Id, name, arguments }, cancellationToken: Lifetime.Token);
        if (!result.Success) throw new InvalidOperationException(result.Content); return result;
    }
    private async Task ShowRoots()
    {
        InvalidateFileBrowserRead();
        fileListing.Clear(); if (fileListingDetails is not null) fileListingDetails.IsExpanded = false;
        if (sharedFiles.IsChecked == true)
        {
            fileEntries.ItemsSource = new[] { new FileEntry(".", "directory", "Shared files root") };
            fileInfo.Text = $"Shared files via '{sharedFilesHost}'. Open the shared root to browse its folders.";
            SetStatus("Shared files root shown. Double-click it to browse."); return;
        }
        var profile = ConversationProfile(); fileEntries.ItemsSource = FileBrowserRoots(profile);
        if (string.IsNullOrWhiteSpace(filePath.Text)) filePath.Text = DefaultFileRoot(profile);
        fileInfo.Text = $"Projects and configured folders on worker '{profile.WorkerId}'. Double-click a root to browse it.";
        SetStatus("Project roots shown. Double-click a project or folder to browse.");
        await Task.CompletedTask;
    }
    private IReadOnlyList<FileEntry> FileBrowserRoots(AgentProfile profile)
    {
        var entries = new List<FileEntry>(); var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var project in navigation.Nodes.Where(x => x.Kind == "project" && x.Path is not null &&
                     profile.AllowedRoots.Any(root => NavigationPathInside(x.Path, root)))
                 .OrderByDescending(x => x.Id == ConversationProjectId).ThenBy(x => x.Title, StringComparer.OrdinalIgnoreCase))
            if (seen.Add(project.Path!)) entries.Add(new(project.Path!, "directory",
                (project.Id == ConversationProjectId ? "Current project · " : "Project · ") + project.Title + (project.CreateWhenUsed ? " · empty" : "")));
        if (profile.WorkerId == "local" && !string.IsNullOrWhiteSpace(projectsDirectory) &&
            profile.AllowedRoots.Any(root => NavigationPathInside(projectsDirectory, root)) && seen.Add(projectsDirectory))
            entries.Add(new(projectsDirectory, "directory", "Projects folder · " + FileBrowserName(projectsDirectory)));
        foreach (var path in profile.AllowedRoots.Where(seen.Add))
            entries.Add(new(path, "directory", "Allowed folder · " + FileBrowserName(path)));
        return entries;
    }
    private static string FileBrowserName(string path)
    {
        var name = path.TrimEnd('/', '\\').Split('/', '\\').LastOrDefault();
        return string.IsNullOrWhiteSpace(name) ? path : name;
    }
    private string DefaultFileRoot(AgentProfile profile)
    {
        if (sharedFiles.IsChecked == true) return ".";
        if (LoadedNativeWorkingRoot(profile) is { } savedRoot) return savedRoot;
        var projectPath = navigation.Nodes.FirstOrDefault(x => x.Kind == "project" && x.Id == ConversationProjectId)?.Path;
        if (projectPath is not null && profile.AllowedRoots.Any(root => NavigationPathInside(projectPath, root))) return projectPath;
        if (profile.WorkerId == "local" && !string.IsNullOrWhiteSpace(projectsDirectory) &&
            profile.AllowedRoots.Any(root => NavigationPathInside(projectsDirectory, root))) return projectsDirectory;
        return profile.AllowedRoots.FirstOrDefault() ?? "";
    }
    private static bool HasSharedGrant(AgentProfile profile, string action) => profile.Capabilities.Contains("shared." + action, StringComparer.Ordinal) &&
        (profile.AllowedTools.Contains("*", StringComparer.Ordinal) || profile.AllowedTools.Contains("shared." + action, StringComparer.Ordinal));
    private string FileTool(string action) => (sharedFiles.IsChecked == true ? "shared." : "file.") + action;
    private void ResetFileSource()
    {
        InvalidateFileBrowserRead();
        fileEntries.ItemsSource = null; fileListing.Clear();
        if (TryConversationProfile() is not AgentProfile profile) { filePath.Clear(); return; }
        filePath.Text = DefaultFileRoot(profile);
        fileInfo.Text = sharedFiles.IsChecked == true ? $"Shared files via '{sharedFilesHost}'. Use relative paths; '.' is the shared root." + (sharedFilesHost == "local" ? $" Local directory: {sharedDirectory}" : "") :
            $"Files on worker '{profile.WorkerId}' within Cerebral's selected project and output roots.";
        if (fileListingDetails is not null) fileListingDetails.IsExpanded = false; UpdateFileLocation();
    }
    private async Task ListFiles()
    {
        var read = await ReadFileBrowserAsync("list");
        var result = CurrentFileBrowserResult(read);
        if (result is null) return;
        fileListing.Text = result.Content;
        var data = Ui.Parse<JsonElement>(result.Content);
        if (data.TryGetProperty("entries", out var entries)) fileEntries.ItemsSource = entries.EnumerateArray().Select(x => new FileEntry(x.GetProperty("path").GetString()!, x.GetProperty("type").GetString()!)).OrderBy(x => x.Type).ThenBy(x => x.Path).ToList();
        else fileEntries.ItemsSource = null;
        if (fileListingDetails is not null) fileListingDetails.IsExpanded = false;
        SetStatus(result.Truncated ? "Directory listing is truncated. Double-click a listed folder to narrow the view." : "Directory listed. Double-click a folder or file to open it.");
    }
    private async Task ReadFile()
    {
        InvalidateFileBrowserRead();
        var read = await ReadFileBrowserAsync("read");
        var result = CurrentFileBrowserResult(read);
        if (result is null) return;
        var selection = read!.Selection;
        if (result.Truncated)
        {
            fileListing.Text = result.Content;
            if (fileListingDetails is not null) fileListingDetails.IsExpanded = true;
            SetStatus("File exceeds the bounded editor read. Use a smaller line range through Tools; the original file cannot be replaced from a partial read.", true);
            return;
        }
        var data = Ui.Parse<JsonElement>(result.Content);
        fileContent.Text = data.GetProperty("content").GetString() ?? ""; openFileHash = data.TryGetProperty("sha256", out var hash) ? hash.GetString() : null;
        openFilePath = selection.Path; openFileProfileId = selection.ProfileId; openFileTruncated = result.Truncated;
        fileInfo.Text = $"{selection.Path} · agent {selection.ProfileName} · {(selection.Shared ? "shared files host " + selection.SharedHost : "worker " + selection.WorkerId)}";
        SetStatus(result.Truncated ? "File read was partial. Use a scoped tool for the complete file before replacing it." : "File read. Save edits checks that the original content still matches.");
    }
    private async Task SaveFile()
    {
        var profile = ConversationProfile();
        if (openFilePath != filePath.Text || openFileProfileId != profile.Id || openFileHash is null) throw new InvalidOperationException("Read this file before replacing it, or use Save as for a new path.");
        if (openFileTruncated) throw new InvalidOperationException("A partial file read cannot be saved over the original file.");
        var result = await CallTool(profile, FileTool("write"), new { path = filePath.Text, content = fileContent.Text, collision = "replace", expectedSha256 = openFileHash });
        var data = Ui.Parse<JsonElement>(result.Content); openFileHash = data.GetProperty("sha256").GetString(); SetStatus("File saved.");
    }
    private async Task SaveAs() => await SaveContent(ConversationProfile(), fileContent.Text, filePath.Text, sharedFiles.IsChecked == true);
    private void PreviewFile()
    {
        var preview = new Window { Owner = this, Icon = Icon, Title = "Markdown preview · " + filePath.Text, Width = 880, Height = 680, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new ScrollViewer { Content = MarkdownView.Render(fileContent.Text, error: ex => SetStatus(ex, true)), Padding = new Thickness(25), VerticalScrollBarVisibility = ScrollBarVisibility.Auto } };
        WindowSizing.Attach(preview); preview.Show();
    }
    private async Task SaveCode(string code, string language)
    {
        var profile = ConversationProfile(); var extension = language.ToLowerInvariant() switch { "csharp" or "cs" => ".cs", "javascript" or "js" => ".js", "python" or "py" => ".py", "json" => ".json", "html" => ".html", "css" => ".css", "markdown" or "md" => ".md", _ => ".txt" };
        if (sharedFiles.IsChecked == true && HasSharedGrant(profile, "write")) { await SaveContent(profile, code, "snippet" + extension, true); return; }
        var root = DefaultFileRoot(profile); var separator = root.Contains('/') && !root.Contains('\\') ? "/" : "\\";
        await SaveContent(profile, code, root.TrimEnd('/', '\\') + separator + "snippet" + extension);
    }
    private async Task SaveContent(AgentProfile profile, string content, string defaultPath, bool shared = false)
    {
        var form = new FieldsDialog(this, "Save file", shared ? "Use a path relative to the configured shared directory. Existing files are not overwritten." : $"The path is on worker '{profile.WorkerId}' and must be inside this agent's allowed roots. Existing files are not overwritten.")
            .Add("path", "Destination path", defaultPath);
        if (form.ShowDialog() != true) return;
        var destination = form.Value("path");
        var lazyProject = !shared ? navigation.Nodes.FirstOrDefault(x => x.Kind == "project" && x.CreateWhenUsed && x.Path is not null &&
            NavigationPathInside(destination, x.Path)) : null;
        if (lazyProject?.Path is { } projectPath)
            await CallTool(profile, "file.mkdir", new { path = projectPath });
        await CallTool(profile, shared ? "shared.write" : "file.write", new { path = destination, content, collision = "fail" });
        if (lazyProject is not null) await ChangeNavigation(lazyProject, copy => copy.CreateWhenUsed = false);
        SetStatus("File saved to " + destination);
    }
    private async Task NewFolder()
    {
        var form = new FieldsDialog(this, "Create folder").Add("path", sharedFiles.IsChecked == true ? "Relative folder path in shared files" : "Full folder path on the service", filePath.Text); if (form.ShowDialog() != true) return;
        await CallTool(ConversationProfile(), FileTool("mkdir"), new { path = form.Value("path") }); SetStatus("Folder created.");
    }
    private async Task TransferTextFile()
    {
        var profile = ConversationProfile(); if (openFilePath != filePath.Text || openFileProfileId != profile.Id || openFileTruncated) throw new InvalidOperationException("Read a complete source file before transferring it.");
        var form = new FieldsDialog(this, "Copy text file to another service", "Copies the complete text shown in the editor using the destination Cerebral agent's saved file grants. Existing destination files are preserved.")
            .Add("url", "Destination service URL", CerebralDefaults.WorkerUrl).Secret("key", "Destination installation access key", "")
            .Add("path", "Destination path", "");
        if (form.ShowDialog() != true) return;
        using var target = new CerebralClient(form.Value("url"), form.SecretValue("key")); var host = await target.GetAsync<HostInfo>("health", cancellationToken: Lifetime.Token);
        if (host.Product != "Pegasus Cerebral" || host.Protocol != 1) throw new InvalidOperationException("Destination is not a compatible Cerebral service.");
        var targetProfiles = await target.GetAsync<List<AgentProfile>>("agents.list", cancellationToken: Lifetime.Token);
        var destinationLeads = targetProfiles.Where(x => x.Role == "lead").ToList();
        if (destinationLeads.Count != 1) throw new InvalidOperationException("Configure exactly one lead Cerebral agent on the destination service before copying files.");
        var destinationProfile = destinationLeads[0];
        await CallTool(destinationProfile, "file.write", new { path = form.Value("path"), content = fileContent.Text, collision = "fail" }, target);
        SetStatus($"Text file copied to {host.Name}: {form.Value("path")}");
    }
    private async Task DiscoverTools()
    {
        var tools = await Get<List<ToolDescriptor>>("tools.list", new { profileId = ConversationProfile().Id });
        toolList.ItemsSource = tools;
        var pao = tools.FirstOrDefault(IsPao);
        toolAvailability.Text = pao is null
            ? "PAO is unavailable to Cerebral. Review the Cortex connection and agent grants in Pegasus Cerebral Configuration."
            : $"PAO available through {pao.Capability}. Cerebral selects user or system-service execution from its saved access state; existing filesystem, Git, process and diagnostics capabilities still apply.";
        SetStatus($"{tools.Count} permitted tool{(tools.Count == 1 ? "" : "s")} discovered" + (pao is null ? "; PAO is unavailable." : "; PAO is available."));
    }
    private static bool IsPao(ToolDescriptor tool) => tool.Name.EndsWith(".pao", StringComparison.Ordinal) || tool.Name.Equals("pao", StringComparison.Ordinal);
    private async Task ExecuteTool()
    {
        var tool = toolList.SelectedItem as ToolDescriptor ?? throw new InvalidOperationException("Select a discovered tool first.");
        try
        {
            var result = await CallTool(ConversationProfile(), tool.Name, Ui.Parse<JsonElement>(toolArguments.Text));
            toolOutput.Text = result.Content; SetStatus(result.Truncated ? "Tool completed; output was bounded." : "Tool completed.");
        }
        catch (Exception ex) { toolOutput.Text = ex.Message; throw; }
    }
    private sealed record FileEntry(string Path, string Type, string? DisplayLabel = null)
    {
        public string Label => DisplayLabel ?? (Type == "directory" ? "Folder · " : "File · ") + FileBrowserName(Path);
    }
}
