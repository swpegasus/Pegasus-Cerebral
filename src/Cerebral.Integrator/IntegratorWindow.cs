using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Cerebral.Core;
using Cerebral.Desktop;
using Microsoft.Win32;

namespace Cerebral.Integrator;

public sealed partial class IntegratorWindow : ConnectedWindow
{
    private HostSettings settings = new();
    private List<ModelRecord> configuredModels = [];
    // Private presentation seam keeps the same draft/save flow in unshown WPF qualification.
    private Func<AgentProfileEditor, Task<bool?>> presentAgentProfile = editor => Task.FromResult(editor.Form.ShowDialog());
    private bool resumeBackgroundSetup;
    private readonly TextBox overview = Ui.Json();
    private readonly TextBox diagnostics = Ui.Json();
    private readonly DataGrid agents = Ui.Grid(("Profile", "Name", 1.5), ("Role", "Role", 1),
        ("Execution machine", "ExecutionMachine", 1.2), ("Inference machine", "InferenceMachine", 1.2),
        ("Model", "Model", 1.5), ("Binding", "Binding", 1), ("Configured slots", "ConfiguredSlots", 1), ("Token budgets", "Budgets", .8), ("Saved tokens", "Tokens", 1.5));
    private readonly DataGrid models = Ui.Grid(("Model", "Name", 1.7), ("Purpose", "Purpose", 1.1), ("Type", "Type", 1.3),
        ("Capabilities", "Capabilities", 1.4), ("Size", "Size", .7), ("Configured context", "Context", 1.6),
        ("Estimated memory", "Capacity", 1.4), ("Configured slots", "ConfiguredSlots", 1), ("Assigned profiles", "AssignedProfiles", 1), ("Saved state", "State", 1));
    private Button? agentEdit;
    private readonly DataGrid workers = Ui.Grid(("Machine", "Name", 2), ("Connection", "Url", 3), ("Needs setup", "RequiresConfiguration", 1));
    private readonly DataGrid jobs = Ui.Grid(("Source", "Source", 1), ("Work", "Title", 2.5), ("Agent", "ProfileId", 1.2), ("Module / kind", "Kind", 1.5), ("Status", "Status", 1), ("ID", "Id", 1.5));
    private readonly DataGrid background = Ui.Grid(("Checklist", "Name", 2), ("Agent", "ProfileId", 2), ("Enabled", "Enabled", 1), ("Interval (min)", "IntervalMinutes", 1), ("Next run", "NextRunAt", 2));
    private readonly TextBlock backgroundState = Ui.Text("Background tasks disabled", 13);
    private readonly DataGrid executables = Ui.Grid(("Tool ID", "Id", 1), ("Executable path", "Path", 4), ("Needs setup", "RequiresConfiguration", 1));
    private readonly DataGrid mcp = Ui.Grid(("Server ID", "Id", 1), ("Enabled", "Enabled", 1), ("User connector", "ViaUserConnector", 1), ("Executable", "Executable", 3), ("HTTP URL", "Url", 2), ("Needs setup", "RequiresConfiguration", 1));
    private readonly TabControl extensionTabs = CreateNestedTabs("ConfigurationExtensionTabs");
    private readonly ConfigurationSettingsNavigation settingsNavigation;

    public IntegratorWindow() : this(true) { }

    internal IntegratorWindow(bool persistWindowPreferences, bool isolatedSettingsFixture = false,
        Cerebral.Desktop.Plugins.DesktopPluginCatalog? desktopPluginCatalog = null, string? desktopPluginDataDirectory = null,
        bool seedBundledDesktopPlugins = true)
        : this(persistWindowPreferences, isolatedSettingsFixture, desktopPluginCatalog, desktopPluginDataDirectory,
            seedBundledDesktopPlugins, managedPluginOwnerBinding: null) { }

    internal IntegratorWindow(bool persistWindowPreferences, bool isolatedSettingsFixture,
        Cerebral.Desktop.Plugins.DesktopPluginCatalog? desktopPluginCatalog, string? desktopPluginDataDirectory,
        bool seedBundledDesktopPlugins, ConfigurationManagedPluginOwnerBinding? managedPluginOwnerBinding)
        : base("Integrator", "Cerebral settings, plugins, and local recovery.", persistWindowPreferences: false)
    {
        desktopCatalog = desktopPluginCatalog ?? new();
        desktopPluginDataRoot = desktopPluginDataDirectory is null
            ? Cerebral.Desktop.Plugins.DesktopPluginCatalog.DefaultDataDirectory : Path.GetFullPath(desktopPluginDataDirectory);
        this.managedPluginOwnerBinding = managedPluginOwnerBinding;
        if (managedPluginOwnerBinding is not null &&
            (!desktopCatalog.RootDirectory.Equals(managedPluginOwnerBinding.CatalogRoot, StringComparison.OrdinalIgnoreCase) ||
             !desktopPluginDataRoot.Equals(managedPluginOwnerBinding.PluginDataRoot, StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("The managed plugin catalog and data must match their validated owner binding.");
        this.seedBundledDesktopPlugins = seedBundledDesktopPlugins;
        isolatedSettingsPresetFixture = isolatedSettingsFixture;
        if (isolatedSettingsFixture) EnterInspectionMode(TimeSpan.FromSeconds(180));
        // Configuration carries wide model/tool tables and plugin settings. Keep a useful desktop default while
        // WindowSizing still contracts it to the monitor work area on 1024-by-768 and other small displays.
        Width = Math.Max(Width, 1360); Height = Math.Max(Height, 880); MinWidth = 1024; MinHeight = 700;
        overview.IsReadOnly = true; diagnostics.IsReadOnly = true;
        var overviewPage = Ui.Page("Local recovery", "Inspect local connection and logs while Cerebral is offline, or manage the existing Worker.", OverviewContent(),
            Ui.Button("Refresh", () => Run(RefreshOverview)),
            SettingsMoreButton("Service actions ▾",
                ("Retry selected Worker", StartLocalWorker), ("Stop local worker", StopLocalWorker),
                ("Open Pegasus Cerebral", OpenCerebralApplication),
                ("Set up installed Worker…", SetupInstalledWorker), ("Installed service status", () => Manage("status")),
                ("Start installed service", () => Manage("start")), ("Stop installed service", () => Manage("stop")),
                ("Install / update…", Install), ("Service connection settings…", EditSettings)));
        ConfigureSettingsItemActions(agents,
            ("Edit", () => EditAgent(SelectedAgent())), ("Open model", OpenSelectedAgentModel),
            ("Advanced settings…", () => EditAgentAdvanced(SelectedAgent())), ("Inspect JSON…", EditAgentJson),
            ("Permissions…", () => EditAgent(SelectedAgent(), startPermissions: true)), ("Delete", DeleteAgent));
        agentEdit = Ui.Button("Edit", () => Run(() => EditAgent(SelectedAgent())));
        System.Windows.Automation.AutomationProperties.SetAutomationId(agentEdit, "ConfigurationAgentEdit");
        agents.SelectionChanged += (_, _) => RefreshAgentSelectionAction();
        ServiceStatusChanged += (_, _) => RefreshAgentSelectionAction();
        var agentsPage = Ui.Page("Agent profiles", "Reusable profiles define a role and execution/inference routes. Each profile can have multiple independent contexts; assigned profiles are not working-agent counts.", agents,
            Ui.Button("New profile", () => Run(NewAgent), true), Ui.Button("Set up Cerebral…", () => Run(SetupOrchestrator)),
            agentEdit!);
        RefreshAgentSelectionAction();
        modelUse = Ui.Button("Use for Pegasus Cerebral", () => Run(UseSelectedModel));
        modelLoad = Ui.Button("Load / unload", () => Run(ToggleSelectedModel));
        modelDetails = Ui.Button("Details / more…", () => Run(ModelDetailsOrMore));
        var modelsPage = Ui.Page("Model library", "Choose models for Cerebral and its agents. Agents sharing a model keep separate request contexts.", ModelLibraryContent(),
            Ui.Button("Add models…", () => Run(AddModels), true), Ui.Button("Set up Cerebral…", () => Run(SetupOrchestrator)),
            modelUse!, modelLoad!, modelDetails!);
        ConfigureSettingsItemActions(workers,
            ("Rename", () => RenameWorker(Selected<WorkerRegistration>(workers))),
            ("Edit connection…", () => EditWorker(Selected<WorkerRegistration>(workers))),
            ("Worker settings…", ConfigureRemoteWorker), ("Probe connection", ProbeWorker),
            ("Remove", RemoveWorker), ("SSH recovery…", RemoteManagement));
        var machinesPage = Ui.Page("Machines", "Select a machine to see its models, configured inference slots and assigned profiles. Runtime observations and measured capacity stay separate.", ComputeMachinesContent(),
            Ui.Button("Open model library", () => Run(OpenComputeMachineModels), true),
            Ui.Button("Import Worker…", () => Run(() => ImportProvisionedWorker())),
            Ui.Button("Add connection manually…", () => Run(() => EditWorker(new WorkerRegistration()))));
        ConfigureSettingsItemActions(peerGrants, ("Rotate token", RotatePeerGrant),
            ("Pause / resume", TogglePeerGrantDrain), ("Revoke", RevokePeerGrant));
        var peerSharingPage = Ui.Page("Peer sharing", "Choose which local agents another Cerebral installation can use. Private prompts, paths, plugins, and administration stay local.", peerGrants,
            Ui.Button("Create grant", () => Run(CreatePeerGrant), true), Ui.Button("Refresh", () => Run(RefreshPeerGrants)));
        var toolsLayout = new Grid();
        toolsTopRow = new() { Height = new GridLength(toolsPreferredTopShare, GridUnitType.Star), MinHeight = 80 };
        toolsBottomRow = new() { Height = new GridLength(1 - toolsPreferredTopShare, GridUnitType.Star), MinHeight = 80 };
        toolsLayout.RowDefinitions.Add(toolsTopRow);
        toolsLayout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(5) });
        toolsLayout.RowDefinitions.Add(toolsBottomRow);
        ConfigureSettingsItemActions(executables,
            ("Edit…", () => EditExecutable(Selected<ExecutableRegistration>(executables))), ("Remove", RemoveExecutable));
        var execPanel = Ui.Page("Registered executables", "Arguments are separate values. Right-click a tool to edit or remove its registration.", executables,
            Ui.Button("Add", () => Run(() => EditExecutable(new ExecutableRegistration()))));
        ConfigureSettingsItemActions(mcp,
            ("Edit…", () => EditMcp(Selected<McpRegistration>(mcp))), ("Remove", RemoveMcp));
        var mcpPanel = Ui.Page("MCP connections", "User-bound providers use the independent user-session connector. Right-click a connection for its settings.", mcp,
            Ui.Button("Add", () => Run(() => EditMcp(new McpRegistration()))));
        Grid.SetRow(execPanel, 0); Grid.SetRow(mcpPanel, 2); toolsLayout.Children.Add(execPanel); toolsLayout.Children.Add(mcpPanel);
        var toolsDivider = CreateSettingsRowDivider("ConfigurationToolsDivider", "Resize executable tools and MCP providers",
            toolsTopRow, toolsBottomRow, share =>
            {
                if (Math.Abs(toolsPreferredTopShare - share) < .000001) return;
                toolsPreferredTopShare = share; SettingsPaneChanged();
            });
        Grid.SetRow(toolsDivider, 1); toolsLayout.Children.Add(toolsDivider);
        toolsLayout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var declarations = new Expander { Header = "MCP provider declarations", Content = McpDeclarationsPage(),
            MaxHeight = 280, Margin = new Thickness(20, 0, 20, 12) };
        System.Windows.Automation.AutomationProperties.SetAutomationId(declarations, "McpProviderDeclarations");
        System.Windows.Automation.AutomationProperties.SetName(declarations, "MCP provider declarations");
        Grid.SetRow(declarations, 3); toolsLayout.Children.Add(declarations);
        workDetails = Ui.Button("Details", () => Run(JobDetails));
        workStop = Ui.Button("Stop selected", () => Run(CancelJob));
        RefreshWorkSelectionActions();
        var queuePage = Ui.Page("Active work", "Unfinished workflows and jobs. Finished work leaves this bounded queue; existing waiting work remains available.", QueueContent(),
            Ui.Button("Refresh", () => Run(RefreshJobs)), workDetails, workStop,
            SettingsMoreButton("History & settings ▾", ("Recent workflows…", ShowWorkflowHistory),
                ("Previous job history…", ShowJobHistory), ("Resources & queues", () => { SelectTab("Resources"); return Task.CompletedTask; }),
                ("Check outcome", ReconcileJob)));
        ConfigureSettingsItemActions(background, ("Edit…", () => EditBackground(Selected<BackgroundTaskDefinition>(background))),
            ("Remove", RemoveBackground));
        var backgroundPage = Ui.Page("Idle checklists", "Optional scheduled work using a configured agent and its existing permissions.", background,
            Ui.Button("Add checklist", () => Run(() => EditBackground(new BackgroundTaskDefinition())), true),
            Ui.Button("Enable / disable all", () => Run(ToggleBackground)), backgroundState);
        var diagnosticsPage = Ui.Page("Command console", "Advanced structured service commands. Results appear here without screen automation.", diagnostics,
            Ui.Button("Health", () => Run(() => Inspect("health"))), Ui.Button("Resources", () => Run(() => Inspect("resources"))),
            Ui.Button("Command…", () => Run(AdvancedCommand)),
            SettingsMoreButton("Inspect & edit ▾", ("Tool metadata", ShowDiagnosticTools),
                ("MCP declarations", () => Inspect("plugins.list")), ("Full settings JSON…", EditSettingsJson)));

        var systemTabs = CreateNestedTabs("ConfigurationSystemTabs");
        AddNestedTab(systemTabs, "General", SettingsPage(), "Settings");
        AddNestedTab(systemTabs, "Appearance", AppearanceSettingsPage(), "Appearance");
        AddNestedTab(systemTabs, "Chats", ChatSettingsPage(), "Chats");
        AddNestedTab(systemTabs, "Backup & restore", BackupSettingsPage(), "Backup");

        var modelAndAgentTabs = CreateNestedTabs("ConfigurationModelsAndAgentsTabs");
        AddNestedTab(modelAndAgentTabs, "Machines", machinesPage, "Machines");
        AddNestedTab(modelAndAgentTabs, "Models", modelsPage, "Models");
        AddNestedTab(modelAndAgentTabs, "Agent profiles", agentsPage, "Agents");
        AddNestedTab(modelAndAgentTabs, "Roles & instructions", InstructionsPage(), "Instructions");
        DescribeComputeNavigation(modelAndAgentTabs);

        AddNestedTab(extensionTabs, "Desktop plugins", DesktopPluginsPage(), "DesktopPlugins");
        AddNestedTab(extensionTabs, "Service extensions", ServiceExtensionsPage(), "ServiceExtensions");
        AddNestedTab(extensionTabs, "Skills", SkillsPage(), "Skills");
        AddNestedTab(extensionTabs, "Tools & MCP", toolsLayout, "ToolsAndMcp");

        var dataAndDiagnosticsTabs = CreateNestedTabs("ConfigurationDataAndDiagnosticsTabs");
        AddNestedTab(dataAndDiagnosticsTabs, "Active work", queuePage, "Queue");
        AddNestedTab(dataAndDiagnosticsTabs, "Idle checklists", backgroundPage, "Background");

        var connectionTabs = CreateNestedTabs("ConfigurationConnectionsTabs");
        AddNestedTab(connectionTabs, "Cortex", CortexPage(), "Cortex");
        AddNestedTab(connectionTabs, "User connector", ConnectorPage(), "UserConnector");
        AddNestedTab(connectionTabs, "Peer sharing", peerSharingPage, "PeerSharing");

        var advancedTabs = CreateNestedTabs("ConfigurationAdvancedTabs");
        AddNestedTab(advancedTabs, "Folders & runtime", FoldersSettingsPage(), "Folders");
        AddNestedTab(advancedTabs, "Resources & queues", ResourceSettingsPage(), "Resources");
        AddNestedTab(advancedTabs, "History & cache", RetentionSettingsPage(), "Retention");
        AddNestedTab(advancedTabs, "System access", AccessSettingsPage(), "Access");
        AddNestedTab(advancedTabs, "Engine", EnginePage(), "Engine");
        AddNestedTab(advancedTabs, "Local recovery", overviewPage, "Dashboard");
        AddNestedTab(advancedTabs, "Command console", diagnosticsPage, "Diagnostics");

        var aboutTabs = CreateNestedTabs("ConfigurationAboutTabs");
        AddNestedTab(aboutTabs, "Pegasus Cerebral", Ui.AboutContent("integrator", typeof(IntegratorWindow).Assembly), "About");

        // Existing category IDs and their order preserve the base window's selected-area preference.
        AddTopLevelTab("General", systemTabs, "System");
        AddTopLevelTab("Compute & agents", modelAndAgentTabs, "ModelsAndAgents");
        AddTopLevelTab("Plugins & tools", extensionTabs, "ToolsExtensionsAndSkills");
        AddTopLevelTab("Activity", dataAndDiagnosticsTabs, "DataAndDiagnostics");
        AddTopLevelTab("Connections", connectionTabs, "Connections");
        AddTopLevelTab("Advanced", advancedTabs, "Advanced");
        AddTopLevelTab("About", aboutTabs, "About");
        if (!isolatedSettingsPresetFixture) InitializeConfigurationDesktopPlugins();
        InitializeSettingsLayout(persistWindowPreferences);
        InitializeNestedTabPreferences(persistWindowPreferences, ReadSettingsLayoutSelection, RecordSettingsLayoutSelection);
        // Retain the native stable-ID selection model while giving Settings its own navigation surface.
        if (Tabs.Parent is not Panel layout) throw new InvalidOperationException("Configuration requires its native application layout.");
        var navigationIndex = layout.Children.IndexOf(Tabs);
        layout.Children.Remove(Tabs);
        settingsNavigation = new ConfigurationSettingsNavigation(Tabs);
        settingsNavigation.PaneSizeChanged += SettingsPaneChanged;
        layout.Children.Insert(navigationIndex, settingsNavigation);
        Closed += (_, _) => settingsNavigation.Dispose();
        InitializeReceiverPageControl();
        RefreshNormalControlPages();
        // Observe transitions even when no control caller polls during an offline/online
        // cycle. A retired owner capsule cannot become current again by returning to it.
        ServiceStatusChanged += (_, _) => { _ = normalControlOwner?.MatchesOwner(); };
    }

    private void AddTopLevelTab(string title, UIElement content, string automationSuffix)
    {
        var tab = Ui.TabPage(title, content, new Thickness(16, 9, 16, 9));
        System.Windows.Automation.AutomationProperties.SetAutomationId(tab, "ConfigurationArea" + automationSuffix);
        Tabs.Items.Add(tab);
    }

    private static TabControl CreateNestedTabs(string automationId)
    {
        var tabs = new TabControl
        {
            TabStripPlacement = Dock.Top,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Stretch,
            Background = Ui.Surface,
            BorderBrush = Ui.Line,
            BorderThickness = new Thickness(0)
        };
        System.Windows.Automation.AutomationProperties.SetAutomationId(tabs, automationId);
        return tabs;
    }

    private static TabItem AddNestedTab(TabControl tabs, string title, UIElement content, string automationSuffix)
    {
        var tab = Ui.TabPage(title, content, new Thickness(14, 9, 14, 9));
        System.Windows.Automation.AutomationProperties.SetAutomationId(tab, "ConfigurationTab" + automationSuffix);
        tabs.Items.Add(tab);
        return tab;
    }

    protected override Task RefreshAfterReconnectAsync()
    {
        // The client can change without an endpoint change. Discard rows before awaited hydration.
        EnsureComputeConnection();
        RefreshAgentSelectionAction();
        RefreshModelSelectionActions();
        return Task.CompletedTask;
    }

    protected override async Task ConnectedAsync(HostInfo info)
    {
        settingsLayoutBackendIdentity = RendererLayoutClient.CaptureBackendIdentity(info, ServiceConnection.Transport, ServiceConnectionIdentity);
        if (isolatedSettingsPresetFixture) { await RefreshSettingsPresetsAsync(); return; }
        await HydrateAppearanceThemeAsync();
        await RefreshAll();
        await RefreshSettingsPresetsAsync();
        // Loaded initially builds the plugin runtime before a connection owns it.
        // Rebind after connection hydration has settled the compute/owner generation.
        if (configurationPluginOwner?.MatchesOwner() != true)
            ReloadConfigurationDesktopPlugins();
    }
    protected override async Task<object> InspectLoadedStateAsync(string? sessionId)
    {
        if (isolatedSettingsPresetFixture) return new { connected = IsConnected, presetsReady = settingsPresetCapabilities is not null };
        await Inspect("diagnostics");
        return new { connected = IsConnected, agents = agents.Items.Count, models = models.Items.Count, jobs = jobs.Items.Count, workers = workers.Items.Count,
            backgroundTasks = background.Items.Count, overviewLoaded = overview.Text.Length > 0, diagnosticsLoaded = diagnostics.Text.Length > 0 };
    }
    private async Task RefreshAll()
    {
        EnsureComputeConnection();
        await RefreshSettings(); await RefreshModels(); await RefreshAgents(); await RefreshJobs(); await RefreshPeerGrants(); await RefreshOverview(); await RefreshInstructionTargets(); await RefreshPlugins(); await RefreshServiceExtensions(); await RefreshSkills(); await RefreshKnowledgeProjects(); await RefreshConnectorAsync(); await RefreshFullAccess(); RefreshSettingsSummary(); await OfferSetupIfNeeded();
    }
    private async Task RefreshSettings()
    {
        var session = await BeginRegistrationEditAsync();
        AdoptRegistrationSettings(session.Settings, session.Connection);
        RefreshModelWorkerChoices();
    }
    private async Task SaveSettings()
    {
        await Command("settings.save", settings); await RefreshSettings(); await RefreshPlugins(); SetStatus("Configuration saved. Listener changes take effect after a service restart.");
    }
    private async Task RefreshAgents()
    {
        var connection = queueConnectionEpoch;
        var owner = Client;
        EnsureComputeConnection();
        var profiles = await ReadAgentOwnerAsync<List<AgentProfile>>(QueueDispatch(), "agents.list", null, Lifetime.Token);
        AdoptComputeProfiles(connection, profiles, owner);
        RefreshAgentSelectionAction();
    }
    private async Task RefreshModels()
    {
        var workerId = SelectedModelWorkerId;
        var connection = queueConnectionEpoch;
        var owner = Client;
        var dispatch = QueueDispatch();
        var epoch = ++modelLibraryRefreshVersion;
        EnsureComputeConnection();
        var selectedId = loadedModelWorkerId == workerId && models.SelectedItem is ModelRow selected ? selected.Record.Id : null;
        refreshingModelLibrary = true;
        models.ItemsSource = null; configuredModels = [];
        RefreshModelSelectionActions();
        try
        {
            var library = await ReadAgentOwnerAsync<List<ModelRecord>>(dispatch,
                workerId == "local" ? "models.list" : "workers.models.list",
                workerId == "local" ? null : new { workerId }, Lifetime.Token);
            if (!IsConnected || !ReferenceEquals(owner, Client) || connection != queueConnectionEpoch ||
                epoch != modelLibraryRefreshVersion || workerId != SelectedModelWorkerId) return;
            if (CurrentComputeProfiles is null)
            {
                var profiles = await ReadAgentOwnerAsync<List<AgentProfile>>(dispatch, "agents.list", null, Lifetime.Token);
                if (!IsConnected || !ReferenceEquals(owner, Client) || connection != queueConnectionEpoch ||
                    epoch != modelLibraryRefreshVersion || workerId != SelectedModelWorkerId) return;
                // A newer profile adoption during this read wins over its fallback result.
                if (CurrentComputeProfiles is null) AdoptComputeProfiles(connection, profiles, owner);
            }
            loadedModelWorkerId = workerId;
            RememberComputeLibrary(connection, workerId, library, owner);
            RefreshDefaultModelPicker();
        }
        finally
        {
            if (epoch == modelLibraryRefreshVersion && IsConnected && ReferenceEquals(owner, Client))
            {
                refreshingModelLibrary = false;
                if (connection == queueConnectionEpoch && workerId == SelectedModelWorkerId)
                {
                    RefreshComputeOverview();
                    models.SelectedItem = (models.ItemsSource as IEnumerable<ModelRow>)?.FirstOrDefault(row => row.Record.Id == selectedId);
                }
                RefreshModelSelectionActions();
            }
        }
    }

    private async Task RefreshOverview()
    {
        var connection = queueConnectionEpoch; var dispatch = QueueDispatch();
        var host = await ReadAgentOwnerAsync<HostInfo>(dispatch, "health", null, Lifetime.Token);
        var resources = await ReadAgentOwnerAsync<ResourceInfo>(dispatch, "resources", null, Lifetime.Token);
        if (connection != queueConnectionEpoch) return;
        EnsureComputeConnection();
        computeResources["local"] = new(connection, DateTimeOffset.UtcNow, resources);
        RefreshComputeOverview();
        overview.Text = $"{host.Name}   ·   {host.OperatingSystem} / {host.Architecture}\nVersion {host.Version}   ·   Protocol {host.Protocol}\nData: {host.DataDirectory}\n\nService process: {resources.ProcessMemoryMb:N0} MiB\nSystem available: {(resources.SystemAvailableMb is { } available ? available.ToString("N0") + " MiB" : "unavailable")}\n\n";
        foreach (var gpu in resources.Gpus)
            overview.AppendText($"{gpu.Name}{(gpu.Unified ? " · unified CPU/GPU memory pool" : "")}\n  Total: {gpu.TotalMb?.ToString("N0") ?? "unavailable"} MiB   Free: {gpu.FreeMb?.ToString("N0") ?? "unavailable"} MiB\n  {gpu.Error}\n");
        if (resources.Gpus.Count == 0) overview.AppendText("GPU telemetry is unavailable. Model loading errors remain visible in the model list.\n");
        var processorSelection = settings.AllowedLogicalProcessors.Count == 0
            ? "all available except logical processor 0"
            : string.Join(", ", settings.AllowedLogicalProcessors.Order());
        overview.AppendText($"\nQueue capacity: {settings.QueueCapacity}  ·  Concurrent jobs: {settings.MaxConcurrentJobs}\nLogical processors requested: {processorSelection}{(settings.MaxLogicalProcessorCount == 0 ? "" : $" · maximum {settings.MaxLogicalProcessorCount}")}\nReserved memory: {settings.ReservedMemoryMb:N0} MiB\nExternal command module: {(settings.ExternalCommandsEnabled ? "enabled" : "disabled")}\nBackground work: {(settings.BackgroundTasksEnabled ? "enabled" : "disabled")}");
        overview.AppendText($"\nShared files host: {settings.SharedFilesWorkerId}\nLocal shared folder: {settings.SharedDirectory}\nShared files remain outside replaceable deployment output; agents need shared.read/shared.write grants.");
    }
    protected override async Task OnEventAsync(ServiceEvent item)
    {
        var refreshInventory = item.Type is "model.updated" or "agents.updated" or "settings.updated" or
            "plugins.updated" or "service-packages.updated";
        // WatchAsync checked this client before dispatch. Keep its exact generation across
        // every await below; a same-endpoint reconnect still replaces the client object.
        var inventoryClient = refreshInventory && IsConnected ? Client : null;
        var inventoryEpoch = queueConnectionEpoch;
        if (item.Type is "plugins.updated" or "settings.updated") { await RefreshSettings(); await RefreshPlugins(); }
        if (item.Type == "service-packages.updated") await RefreshServiceExtensions();
        if (item.Type == "settings.updated") await HydrateAppearanceThemeAsync();
        if (item.Type == "skills.updated") await RefreshSkills();
        if (item.Type == "agents.updated")
        {
            await RefreshSettings(); await RefreshAgents(); await RefreshModels(); await RefreshInstructionTargets();
        }
        if (item.Type == "model.updated") { await RefreshModels(); await RefreshAgents(); }
        // The inventory header is a separate projection from the model and agent grids.
        // Event refresh reads current counts without scanning the repository or changing tabs.
        if (inventoryClient is not null)
            await OfferSetupIfNeeded(refresh: true, scanRepository: false,
                expectedClient: inventoryClient, expectedEpoch: inventoryEpoch);
        if (item.Type is "job.updated" or "workflow.updated") await RefreshJobs();
        if (item.Type == "transfer")
        {
            if (item.Data.TryGetProperty("bytes", out var bytes) && bytes.TryGetInt64(out var sent))
            {
                var expected = item.Data.TryGetProperty("expectedLength", out var length) && length.ValueKind == JsonValueKind.Number && length.TryGetInt64(out var total) ? $" / {total / 1048576d:N1} MiB" : " MiB";
                SetStatus($"Transferring model: {sent / 1048576d:N1}{expected}");
            }
            else if (item.Data.TryGetProperty("retry", out var attempt)) SetStatus($"Model connection interrupted; transfer retry {attempt} in progress.");
        }
    }

    private static T Selected<T>(DataGrid grid) => grid.SelectedItem is T item ? item : throw new InvalidOperationException("Select an item first.");
    private void RefreshAgentSelectionAction()
    {
        if (agentEdit is null) return;
        var reason = !IsConnected ? "Connect to the Worker to edit an agent profile." :
            !ComputeProjectionCurrent ? "Wait for this Worker's agent profiles to load." :
            agents.SelectedItem is not AgentRow row || (agents.ItemsSource as IEnumerable<AgentRow>)?.Contains(row) != true
                ? "Select an agent profile to edit it." : "";
        agentEdit.IsEnabled = reason.Length == 0;
        agentEdit.ToolTip = reason.Length == 0 ? null : reason;
        ToolTipService.SetShowOnDisabled(agentEdit, true);
        System.Windows.Automation.AutomationProperties.SetHelpText(agentEdit, reason);
    }
    private AgentProfile SelectedAgent() => agents.SelectedItem is AgentRow row ? row.Record : throw new InvalidOperationException("Select an agent first.");
    private ModelRecord SelectedModel() => !refreshingModelLibrary && loadedModelWorkerId == SelectedModelWorkerId && models.SelectedItem is ModelRow row
        ? row.Record : throw new InvalidOperationException("Wait for the selected machine's model library, then select a model.");
    private bool Confirm(string text) => MessageBox.Show(this, text, "Pegasus Cerebral", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;

    private async Task NewAgent()
    {
        var templates = configurationPlugins?.AgentTemplates ?? [];
        if (templates.Count == 0)
        {
            await EditAgent(new AgentProfile());
            return;
        }
        const string custom = "Custom agent";
        var choices = new Dictionary<string, Cerebral.Desktop.Plugins.DesktopHostedAgentTemplate?>(StringComparer.Ordinal) { [custom] = null };
        foreach (var template in templates.OrderBy(x => x.PluginId).ThenBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            var basic = $"{template.Name} · {FriendlyAgentRole(template.ExecutionRole)} · plugin {template.PluginId}";
            var label = basic;
            for (var suffix = 2; choices.ContainsKey(label); suffix++) label = $"{basic} ({suffix})";
            choices[label] = template;
        }
        var form = new FieldsDialog(this, "New agent", "Start with a custom profile or behavioral defaults contributed by an enabled plugin. Templates do not grant tools, capabilities, folders, or administrative access.", "Continue") { Height = 340 };
        form.Choice("template", "Agent template", choices.Keys, custom);
        if (form.ShowDialog() != true) return;
        var selected = choices[form.Selection("template")];
        await EditAgent(selected is null ? new AgentProfile() : selected.CreateProfile());
    }

    private async Task EditAgent(AgentProfile original, bool startAdvanced = false, bool startPermissions = false)
    {
        // Capture the selected profile and owner before an awaited refresh can replace UI rows or connection.
        var connection = queueConnectionEpoch;
        var capturedClient = Client;
        var capturedProfile = CerebralJson.Read<AgentProfile>(CerebralJson.Element(original));
        await RefreshSettings();
        if (connection != queueConnectionEpoch || !ReferenceEquals(capturedClient, Client))
            throw new InvalidOperationException("The connection changed. Reopen the agent for the intended service.");
        var dispatch = new Func<CommandRequest, CancellationToken, Task<CommandResult>>(
            (request, token) => capturedClient.CommandAsync(request.Operation, request.Arguments, request.ProfileId, token));
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Lifetime.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        var configuration = await ReadAgentOwnerAsync<HostSettings>(dispatch, "settings.get", null, timeout.Token);
        var configuredAgents = await ReadAgentOwnerAsync<List<AgentProfile>>(dispatch, "agents.list", null, timeout.Token);
        Task<List<ModelRecord>> ReadModels(string workerId, CancellationToken cancellationToken) => ReadAgentOwnerAsync<List<ModelRecord>>(dispatch,
            workerId == "local" ? "models.list" : "workers.models.list",
            workerId == "local" ? null : new { workerId }, cancellationToken);
        List<ModelRecord> defaultModels = [];
        string? defaultError = null;
        try { defaultModels = await ReadModels(configuration.DefaultModelWorkerId, timeout.Token); }
        catch (Exception error) when (error is not OperationCanceledException)
        { defaultError = "The default model machine could not be read: " + error.Message; }
        if (connection != queueConnectionEpoch || !IsConnected || !ReferenceEquals(capturedClient, Client))
            throw new InvalidOperationException("The connection changed. Reopen the agent for the intended service.");
        using var editor = new AgentProfileEditor(this, capturedProfile, configuration, configuredAgents, defaultModels, defaultError,
            ReadModels, dispatch, () => queueConnectionEpoch, connection, capturedClient.IsLocalPipe, Lifetime.Token, startAdvanced,
            startPermissions: startPermissions);
        if (await presentAgentProfile(editor) != true || editor.Acknowledged is not { } saved) return;
        await CompleteAcknowledgedAgentAsync(saved, connection, capturedClient,
            () => RefreshAgentRowsOnConnectionAsync(connection, dispatch, Lifetime.Token, capturedClient),
            ResumeBackgroundSetupIfRequested);
    }
    private static Dictionary<string, string> AgentModelMachineChoices(HostSettings settings, string savedWorkerId)
    {
        var choices = new Dictionary<string, string>(StringComparer.Ordinal) { ["This machine · local pipe"] = "local" };
        foreach (var worker in settings.Workers.Where(x => !x.RequiresConfiguration && !string.IsNullOrWhiteSpace(x.AccessKey))
                     .OrderBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            var basis = WorkerChoiceLabel(worker);
            var label = basis;
            for (var suffix = 2; choices.ContainsKey(label); suffix++) label = basis + $" ({suffix})";
            choices.Add(label, worker.Id);
        }
        if (!choices.ContainsValue(savedWorkerId)) choices.Add("Unavailable saved machine · " + savedWorkerId, savedWorkerId);
        return choices;
    }

    // Owns one editor draft only. Model-library reads never save settings or silently replace a missing binding.
    private sealed class AgentModelBindingEditor : IDisposable
    {
        internal const string FollowDefault = "Use Pegasus Cerebral default";
        internal const string PinnedModel = "Pin this agent to a model";
        internal const string ChooseModel = "Choose a model…";
        private readonly FieldsDialog form;
        private readonly Dictionary<string, string> workerChoices;
        private readonly string defaultWorkerId;
        private readonly ModelRecord? defaultModel;
        private readonly string? defaultError;
        private readonly Func<string, Task<List<ModelRecord>>> readModels;
        private readonly Func<long> connectionEpoch;
        private readonly long acceptedEpoch;
        private readonly bool allowUnchangedConfiguration;
        private readonly string configuredDefaultModelId;
        private readonly string savedModelId;
        private readonly string savedModelWorkerId;
        private readonly bool savedFollowsDefault;
        private string? libraryError;
        private readonly Dictionary<string, string> selectedByWorker = new(StringComparer.Ordinal);
        private readonly Dictionary<string, List<ModelRecord>> initialLibraries = new(StringComparer.Ordinal);
        private Dictionary<string, string> modelChoices = new(StringComparer.Ordinal);
        private List<ModelRecord> availableModels = [];
        private string? loadedWorkerId;
        private int generation;
        private bool updating;
        private bool loading;
        private bool disposed;
        internal Action? Changed { get; set; }
        internal Task PendingLoad { get; private set; } = Task.CompletedTask;
        internal bool FollowsDefault => form.Selection("modelMode") == FollowDefault;
        internal string ModelWorkerId => FollowsDefault ? defaultWorkerId : workerChoices[form.Selection("modelWorker")];
        internal string ModelId => FollowsDefault ? defaultModel?.Id ?? configuredDefaultModelId : modelChoices.GetValueOrDefault(form.Selection("model")) ?? "";
        internal ModelRecord? SelectedModel => FollowsDefault ? defaultModel :
            loadedWorkerId == ModelWorkerId ? availableModels.FirstOrDefault(x => x.Id == ModelId) : null;
        internal Func<bool>? ExternalSaveAllowed { get; set; }
        internal Func<string?>? ExternalSaveStatus { get; set; }
        internal bool SavedSelectionUnchanged => FollowsDefault == savedFollowsDefault &&
            ModelId == savedModelId && ModelWorkerId == savedModelWorkerId;
        internal bool CanSave => !disposed && !loading && connectionEpoch() == acceptedEpoch &&
            (ExternalSaveAllowed?.Invoke() ?? true) &&
            (SelectedModel is not null || allowUnchangedConfiguration && SavedSelectionUnchanged);
        internal void RefreshSaveEligibility() => RefreshState();

        internal AgentModelBindingEditor(FieldsDialog form, AgentProfile profile, Dictionary<string, string> workerChoices,
            string defaultWorkerId, ModelRecord? defaultModel, List<ModelRecord> defaultModels, string? defaultError,
            Func<string, Task<List<ModelRecord>>> readModels, Func<long> connectionEpoch, long acceptedEpoch,
            bool allowUnchangedConfiguration = false, string? configuredDefaultModelId = null)
        {
            this.form = form; this.workerChoices = workerChoices; this.defaultWorkerId = defaultWorkerId;
            this.defaultModel = defaultModel; this.defaultError = defaultError; this.readModels = readModels;
            this.connectionEpoch = connectionEpoch; this.acceptedEpoch = acceptedEpoch;
            this.allowUnchangedConfiguration = allowUnchangedConfiguration;
            this.configuredDefaultModelId = configuredDefaultModelId ?? defaultModel?.Id ?? "";
            savedModelId = profile.ModelId; savedModelWorkerId = profile.ModelWorkerId;
            savedFollowsDefault = profile.FollowsDefaultModel || profile.Role == "lead";
            selectedByWorker[profile.ModelWorkerId] = profile.ModelId;
            if (defaultError is null) initialLibraries[defaultWorkerId] = defaultModels;
            updating = true;
            form.WhenChoiceChanged("modelMode", _ => { if (!updating) RefreshMode(); })
                .WhenChoiceChanged("modelWorker", _ => { if (!updating && !FollowsDefault) BeginLoad(); })
                .WhenChoiceChanged("model", _ =>
                {
                    if (updating || FollowsDefault) return;
                    selectedByWorker[ModelWorkerId] = ModelId; RefreshState();
                });
            updating = false;
            form.Closed += (_, _) => Dispose();
            RefreshMode();
        }

        private void RefreshMode()
        {
            generation++; loading = false;
            form.SetEnabled("modelWorker", !FollowsDefault).SetEnabled("model", !FollowsDefault)
                .SetVisible("modelWorker", !FollowsDefault).SetVisible("model", !FollowsDefault);
            if (connectionEpoch() != acceptedEpoch) { RefreshState(); return; }
            if (FollowsDefault) RefreshState();
            else BeginLoad();
        }

        private void BeginLoad()
        {
            var workerId = ModelWorkerId;
            var current = ++generation;
            loading = true; loadedWorkerId = null; availableModels = []; libraryError = null;
            ReplaceModels(workerId, []);
            form.SetEnabled("model", false).SetCanSubmit(false);
            form.SetValue("routing", "Reading the model library on " + form.Selection("modelWorker") + "…");
            form.SetInlineStatus("Reading the selected machine's models…");
            if (initialLibraries.Remove(workerId, out var cached))
            {
                Adopt(workerId, cached); PendingLoad = Task.CompletedTask; return;
            }
            PendingLoad = LoadAsync(workerId, current);
        }

        private async Task LoadAsync(string workerId, int current)
        {
            try
            {
                var models = await readModels(workerId);
                if (disposed || current != generation || FollowsDefault || workerId != ModelWorkerId) return;
                if (connectionEpoch() != acceptedEpoch)
                {
                    loading = false;
                    form.SetCanSubmit(false).SetInlineStatus("The service connection changed. Reopen this editor; the draft has not been saved.", true);
                    return;
                }
                Adopt(workerId, models);
            }
            catch (Exception error)
            {
                if (disposed || current != generation || FollowsDefault || workerId != ModelWorkerId) return;
                loading = false; loadedWorkerId = null; availableModels = [];
                form.SetEnabled("model", false).SetCanSubmit(false);
                form.SetValue("routing", "Model library unavailable on " + form.Selection("modelWorker") + ". Saved model: " +
                    (selectedByWorker.GetValueOrDefault(workerId) ?? "none") + ".");
                libraryError = "Could not read that machine's model library: " + error.Message;
                RefreshState();
            }
        }

        private void Adopt(string workerId, List<ModelRecord> models)
        {
            loading = false; loadedWorkerId = workerId; availableModels = models; libraryError = null;
            ReplaceModels(workerId, models);
            form.SetEnabled("model", true); RefreshState();
        }

        private void ReplaceModels(string workerId, List<ModelRecord> models)
        {
            var desired = selectedByWorker.GetValueOrDefault(workerId) ?? "";
            modelChoices = new Dictionary<string, string>(StringComparer.Ordinal) { [ChooseModel] = "" };
            foreach (var item in ModelChoices(models)) modelChoices.Add(item.Key, item.Value);
            if (desired.Length > 0 && !modelChoices.ContainsValue(desired))
                modelChoices.Add("Unavailable saved model · " + desired, desired);
            var selection = modelChoices.First(item => item.Value == desired).Key;
            updating = true;
            try { form.SetChoices("model", modelChoices.Keys, selection); }
            finally { updating = false; }
        }

        private void RefreshState()
        {
            var model = SelectedModel;
            var canSave = CanSave;
            form.SetCanSubmit(canSave);
            var machine = FollowsDefault ? "Cerebral default · " +
                (workerChoices.FirstOrDefault(x => x.Value == defaultWorkerId).Key ?? defaultWorkerId) : form.Selection("modelWorker");
            var configurationOnly = canSave && model is null;
            form.SetValue("routing", model is null
                ? machine + "\nSaved model: " + ModelId + "\n" +
                    (FollowsDefault ? defaultError ?? "Model information is unavailable." :
                        libraryError ?? (ModelId.Length == 0 ? "Choose a model explicitly; no model is selected automatically." : "The saved model is unavailable on this machine."))
                : machine + "\n" + model.Name + "\n" + CapabilitySummary(model) + "\nModel inference placement is separate from agent execution and tool access.");
            var externalStatus = ExternalSaveStatus?.Invoke();
            form.SetInlineStatus(externalStatus ?? (configurationOnly
                ? "Unchanged saved configuration may be edited. The Worker revalidates on Save; remote owners must remain reachable."
                : canSave ? "" : connectionEpoch() != acceptedEpoch
                    ? "The service connection changed. Reopen the editor; the draft has not been saved."
                    : libraryError ?? defaultError ?? "An available model route is required before changing model routing."),
                externalStatus is not null || !canSave);
            Changed?.Invoke();
        }

        public void Dispose() { if (disposed) return; disposed = true; generation++; }
    }
    private Task EditAgentAdvanced(AgentProfile original) => EditAgent(original, startAdvanced: true);
    private void EditBrowserSettings(AgentProfile profile)
    {
        var value = profile.PluginSettings.TryGetValue("pegasus.browser", out var configured) && configured.ValueKind == JsonValueKind.Object
            ? configured
            : default;
        int Number(string name, int fallback) => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var item) && item.TryGetInt32(out var number) ? number : fallback;
        string Text(string name, string fallback) => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var item) && item.ValueKind == JsonValueKind.String ? item.GetString() ?? fallback : fallback;
        var domains = value.ValueKind == JsonValueKind.Object && value.TryGetProperty("allowedDomains", out var domainValue) && domainValue.ValueKind == JsonValueKind.Array
            ? domainValue.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String).Select(item => item.GetString() ?? "")
            : Array.Empty<string>();
        var choices = new[] { "deny", "confirm" };
        var form = new FieldsDialog(this, "Browser capacity and safety limits",
            "These limits apply to this agent's isolated browser work. Cerebral-wide concurrent agent/job limits remain in Advanced > Resources & queues. Browser actions are currently serialized; native browser-process CPU/RAM limits are unavailable, so page JavaScript heap is the enforceable memory ceiling.")
            .Add("jobs", "Concurrent browser sessions", Number("maximumJobs", 2).ToString())
            .Add("tabsPerJob", "Maximum tabs per session", Number("maximumTabsPerJob", 3).ToString())
            .Add("tabs", "Maximum tabs across this agent", Number("maximumTabs", 6).ToString())
            .Add("queue", "Waiting browser actions", Number("queueDepth", 8).ToString())
            .Add("delay", "Minimum delay between actions (milliseconds)", Number("minimumActionDelayMilliseconds", 350).ToString())
            .Add("navigation", "Navigation timeout (seconds)", Number("navigationTimeoutSeconds", 30).ToString())
            .Add("action", "Action timeout (seconds)", Number("actionTimeoutSeconds", 15).ToString())
            .Add("idle", "Close idle sessions after (seconds)", Number("idleTimeoutSeconds", 300).ToString())
            .Add("heap", "Page JavaScript heap limit (MiB)", Number("pageHeapLimitMiB", 128).ToString())
            .Add("downloadFile", "Maximum confirmed download file (bytes)", Number("downloadFileBytes", 16 * 1024 * 1024).ToString())
            .Add("downloadQuota", "Confirmed download quota per session (bytes)", Number("downloadQuotaBytes", 64 * 1024 * 1024).ToString())
            .Add("screenshotQuota", "Screenshot storage quota (bytes)", Number("screenshotQuotaBytes", 16 * 1024 * 1024).ToString())
            .Add("screenshotRetention", "Screenshot retention (minutes)", Number("screenshotRetentionMinutes", 30).ToString())
            .Add("domains", "Allowed exact domains or IP addresses, one per line", string.Join(Environment.NewLine, domains), true)
            .Choice("downloads", "Downloads", choices, Text("downloads", "confirm"))
            .Choice("uploads", "Uploads", choices, Text("uploads", "confirm"))
            .Choice("clipboard", "Clipboard", choices, Text("clipboard", "deny"))
            .Choice("authentication", "Authentication prompts", choices, Text("authentication", "confirm"))
            .Choice("externalApplications", "External applications", choices, Text("externalApplications", "deny"))
            .Choice("outsideDomains", "Navigation outside allowed domains", choices, Text("outsideDomains", "confirm"));
        if (form.ShowDialog() != true) return;
        var maximumTabs = form.Number("tabs", 1, 16);
        var maximumTabsPerJob = form.Number("tabsPerJob", 1, 8);
        if (maximumTabsPerJob > maximumTabs) throw new ArgumentException("Maximum tabs per session cannot exceed the agent's total browser tab limit.");
        var allowedDomains = form.Lines("domains");
        if (allowedDomains.Count > 32) throw new ArgumentException("Browser settings permit at most 32 exact domains or IP addresses.");
        profile.PluginSettings["pegasus.browser"] = CerebralJson.Element(new
        {
            maximumJobs = form.Number("jobs", 1, 4),
            maximumTabsPerJob,
            maximumTabs,
            maximumActionsPerSession = 1,
            queueDepth = form.Number("queue", 0, 16),
            minimumActionDelayMilliseconds = form.Number("delay", 100, 5000),
            navigationTimeoutSeconds = form.Number("navigation", 2, 120),
            actionTimeoutSeconds = form.Number("action", 1, 60),
            idleTimeoutSeconds = form.Number("idle", 30, 1800),
            pageHeapLimitMiB = form.Number("heap", 16, 1024),
            downloadFileBytes = form.Number("downloadFile", 1, 268_435_456),
            downloadQuotaBytes = form.Number("downloadQuota", 1, 1_073_741_824),
            screenshotQuotaBytes = form.Number("screenshotQuota", 262_144, 67_108_864),
            screenshotRetentionMinutes = form.Number("screenshotRetention", 1, 1440),
            allowedDomains,
            downloads = form.Selection("downloads"),
            uploads = form.Selection("uploads"),
            clipboard = form.Selection("clipboard"),
            authentication = form.Selection("authentication"),
            externalApplications = form.Selection("externalApplications"),
            outsideDomains = form.Selection("outsideDomains")
        });
    }
    private async Task EditAgentJson()
    {
        var profile = SelectedAgent(); var updated = EditJson("Agent configuration", profile);
        if (updated is null) return; if (updated.Id != profile.Id) throw new ArgumentException("Keep the existing agent ID.");
        await Command("agents.save", updated); await RefreshAgents(); SetStatus("Agent saved.");
    }
    private async Task DeleteAgent()
    {
        var profile = SelectedAgent(); if (!Confirm($"Delete the configured agent '{profile.Name}'?")) return;
        await Command("agents.delete", new { id = profile.Id }); await RefreshAgents(); SetStatus("Agent deleted.");
    }
    private async Task OpenSelectedAgentModel()
    {
        var profile = SelectedAgent();
        var modelId = profile.FollowsDefaultModel ? settings.DefaultModelId : profile.ModelId;
        var workerId = profile.FollowsDefaultModel ? settings.DefaultModelWorkerId : profile.ModelWorkerId;
        if (!(modelWorkerPicker.ItemsSource as IEnumerable<ModelWorkerChoice>)!.Any(x => x.Id == workerId))
            throw new InvalidOperationException("This model's worker has no coordinator management connection. Import its provisioning invitation first.");
        changingModelWorkerChoices = true;
        try { modelWorkerPicker.SelectedValue = workerId; } finally { changingModelWorkerChoices = false; }
        await RefreshModels();
        models.SelectedItem = (models.ItemsSource as IEnumerable<ModelRow>)?.FirstOrDefault(x => x.Record.Id == modelId)
            ?? throw new InvalidOperationException("The model selected by this agent is not registered on its configured machine.");
        SelectTab("Models");
        models.ScrollIntoView(models.SelectedItem);
        SetStatus($"Showing the model used by {profile.Name}.");
    }
    private async Task ImportModel()
    {
        var owner = Client; var connection = queueConnectionEpoch;
        var policy = new ModelImportPolicy(owner.IsLocalPipe);
        var form = new FieldsDialog(this, "Import model", policy.Description)
            .Choice("source", "Source method", policy.Choices, policy.Default).Add("path", "Source file path", "")
            .ActionButton("Browse this computer…", dialog =>
            {
                var picker = new OpenFileDialog { Title = "Select a GGUF model path on this computer", Filter = "GGUF model (*.gguf)|*.gguf|All files (*.*)|*.*", CheckFileExists = true };
                if (picker.ShowDialog(dialog) == true) dialog.SetValue("path", picker.FileName);
            })
            .Add("name", "Display name", "").Add("license", "License / terms reference (optional)", "");
        if (form.ShowDialog() != true) return;
        var method = policy.Resolve(form.Selection("source"));
        if (!IsConnected || connection != queueConnectionEpoch || !ReferenceEquals(owner, Client))
            throw new InvalidOperationException("The model connection changed. Reopen import on the intended service. No import request was sent.");
        var displayName = string.IsNullOrWhiteSpace(form.Value("name")) ? Path.GetFileNameWithoutExtension(form.Value("path")) : form.Value("name");
        if (method == ModelImportMethod.Upload)
        {
            SetStatus("Uploading the model from your user session into the service repository…");
            await owner.UploadModelAsync(form.Value("path"), displayName, form.Value("license"), Lifetime.Token);
        }
        else
        {
            SetStatus("Importing the service-accessible model path…");
            var result = await owner.CommandAsync("models.import", new { path = form.Value("path"), name = displayName, license = form.Value("license"), copy = method == ModelImportMethod.Copy }, cancellationToken: Lifetime.Token);
            if (!result.Success) throw new InvalidOperationException(result.Error ?? "Model import was not confirmed.");
        }
        if (!IsConnected || connection != queueConnectionEpoch || !ReferenceEquals(owner, Client))
        { SetStatus("The previous service confirmed the model import. Reconnect to that service to review its model library."); return; }
        await RefreshModels(); SetStatus("Model imported.");
    }
    private async Task DownloadModel()
    {
        var form = new FieldsDialog(this, "Download model", "Use the direct file URL from the model publisher. The service reports incomplete or unavailable downloads.")
            .Add("url", "Direct HTTP/HTTPS URL", "").Add("name", "Model name", "").Add("file", "Destination file name (including .gguf)", "model.gguf");
        if (form.ShowDialog() != true) return;
        SetStatus("Downloading model. Do not submit a second download while this one is active…");
        await Command("models.download", new { url = form.Value("url"), name = form.Value("name"), fileName = form.Value("file") });
        await RefreshModels(); SetStatus("Download completed and model registered.");
    }
    private async Task ModelCommand(string action)
    {
        var model = SelectedModel();
        if (action == "delete" && !Confirm($"Remove model '{model.Name}' from the service? Inspect its stored path before removing any model you still need.")) return;
        SetStatus($"Model {action} in progress…"); await ModelLibraryCommand("models." + action, new { id = model.Id }); await RefreshModels(); SetStatus($"Model {action} completed.");
    }
    private async Task DistributeModel()
    {
        var model = SelectedModel();
        if (loadedModelWorkerId != "local") throw new InvalidOperationException("Select this machine before distributing a local model.");
        var form = new FieldsDialog(this, "Distribute model", "Verify this model and its image component on a configured LAN worker. Existing identical resources are reused without changing that worker's settings.")
            .Add("worker", "Worker ID", settings.Workers.FirstOrDefault()?.Id ?? "");
        if (form.ShowDialog() != true) return;
        var workerId = form.Value("worker");
        SetStatus("Verifying installed model resources on the worker…");
        var result = await Command("models.distribute", new { id = model.Id, workerId });
        var receipt = CerebralJson.Read<ModelDistributionReceipt>(result.Data);
        if (receipt.State != "ExistingVerified" || receipt.BytesTransferred != 0 || receipt.TargetSettingsChanged ||
            receipt.SourceModelId != model.Id || receipt.WorkerId != workerId || string.IsNullOrWhiteSpace(receipt.TargetModelId))
            throw new InvalidDataException("The worker did not confirm matching installed model resources. No transfer success was reported.");
        Ui.ShowText(this, "Existing model verified",
            $"{model.Name} is already installed and verified on {workerId}.\n\nNo files were transferred. The existing worker model and runtime settings were retained.\n\n" +
            $"Worker model: {receipt.TargetModelId}\nWeights: {receipt.Weights.Bytes:N0} bytes\n" +
            (receipt.Projector is { } projector ? $"Image component: {projector.Bytes:N0} bytes" : "Image component: none selected"));
        SetStatus($"Existing model verified on {workerId}. No files transferred.");
    }
    private async Task EditModel()
    {
        using var capacityDraft = capacityDraftGate.Enter(modelCapacityControl?.Presenter);
        var connection = queueConnectionEpoch; var workerId = loadedModelWorkerId;
        var model = Ui.Parse<ModelRecord>(Ui.Serialize(SelectedModel()));
        const string automatic = "Automatic — use available memory";
        const string fixedContext = "Fixed — use the exact context below";
        var form = new FieldsDialog(this, "Model runtime settings", "Unload the model before changing runtime settings. Automatic sizing reads current memory at each load and preserves the service headroom setting. Every parallel request slot needs its own context cache.\n\n" + ConciseModelSummary(model))
            .Add("name", "Display name", model.Name)
            .Add("projector", "Image understanding component path on the selected machine (matching projector GGUF; blank disables image input)", model.VisionProjectorPath ?? "")
            .Choice("sizing", "Context sizing", [automatic, fixedContext], model.AutomaticContextSizing ? automatic : fixedContext)
            .Add("context", "Fixed context tokens (also shows the last automatic choice)", model.ContextTokens.ToString())
            .Add("maximum", "Automatic context ceiling (0 = detected native limit)", model.MaximumContextTokens.ToString())
            .Add("parallel", "Simultaneous inference request slots", model.ParallelRequests.ToString()).Add("layers", "GPU layers (-1 = automatic fit, 0 = CPU only)", model.GpuLayers.ToString())
            .Add("idle", "Idle unload minutes (-1 = service default, 0 = never)", model.IdleMinutes.ToString()).Add("license", "License / terms reference", model.License ?? "")
            .Add("template", "Chat template override (leave blank to use an embedded template)", model.ChatTemplate ?? "", true)
            .Add("stop", "Stop sequences (JSON string array; \\n preserves an embedded newline)", Ui.Serialize(model.StopSequences), true);
        if (form.ShowDialog() != true) return;
        if (connection != queueConnectionEpoch || workerId != loadedModelWorkerId || workerId != SelectedModelWorkerId) throw new InvalidOperationException("The model connection changed. Reopen settings for the intended machine.");
        model.Name = form.Value("name"); model.AutomaticContextSizing = form.Selection("sizing") == automatic;
        model.VisionProjectorPath = string.IsNullOrWhiteSpace(form.Value("projector")) ? null : form.Value("projector");
        model.ContextTokens = form.Number("context", 512, 1_048_576); model.MaximumContextTokens = form.Number("maximum", 0, 1_048_576); model.ParallelRequests = form.Number("parallel", 1, 16);
        model.GpuLayers = form.Number("layers", -1, 10000); model.IdleMinutes = form.Number("idle", -1, 10080); model.License = form.Value("license");
        model.ChatTemplate = string.IsNullOrWhiteSpace(form.Raw("template")) ? null : form.Raw("template"); model.StopSequences = Ui.Parse<List<string>>(form.Raw("stop"));
        var saved = await ModelLibraryGet<ModelRecord>("models.save", model, workerId);
        var requestedComponent = model.VisionProjectorPath; var confirmedComponent = saved.VisionProjectorPath;
        var localPathSyntax = workerId == "local" && requestedComponent is not null && confirmedComponent is not null &&
            Path.IsPathFullyQualified(requestedComponent) && Path.IsPathFullyQualified(confirmedComponent);
        if (localPathSyntax)
        {
            requestedComponent = Path.GetFullPath(requestedComponent!);
            confirmedComponent = Path.GetFullPath(confirmedComponent!);
        }
        if (!string.Equals(confirmedComponent, requestedComponent, localPathSyntax && OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new InvalidOperationException("The service did not confirm the selected image component. Reconnect to an updated Worker and review the model settings.");
        if (connection != queueConnectionEpoch) { SetStatus("The previously selected service confirmed the model settings. Reconnect to review them."); return; }
        await RefreshModels(); SetStatus("Model runtime settings saved. Image support is verified only when the matching runtime prepares an image.");
    }
    private async Task EditSettings()
    {
        RequireWorkerManagementAllowed("change the service connection");
        var session = await BeginRegistrationEditAsync();
        var form = CreateServiceSettingsForm(session.Settings);
        await ShowAutoSavedSettingsAsync(session, form, baseline => ServiceSettingsCandidate(baseline, form));
        await RefreshOverview();
    }

    private FieldsDialog CreateServiceSettingsForm(HostSettings current) =>
        new FieldsDialog(this, "Service connection settings",
            "Advanced service identity, listener, and shared-file routing. Listener changes apply after a Worker restart. Folders, resources, and question behavior have separate settings pages. Installation credentials are preserved; change them only through the explicit full-settings editor.")
            .Add("name", "Machine name", current.Name)
            .Add("url", "Listen URL", current.ListenUrl)
            .Check("lan", "Allow local-network access", current.AllowLan)
            .Check("external", "Enable optional MCP / CLI command module", current.ExternalCommandsEnabled)
            .Check("coordinator", "This service owns the lead conversation", current.Coordinator)
            .Add("sharedHost", "Shared files host (local or a configured coordinator worker ID)", current.SharedFilesWorkerId);

    private static HostSettings ServiceSettingsCandidate(HostSettings baseline, FieldsDialog form)
    {
        // Credential changes require explicit submission, never a typing pause in an autosave editor.
        var candidate = CopyHostSettings(baseline);
        candidate.Name = form.Value("name"); candidate.ListenUrl = form.Value("url");
        candidate.AllowLan = form.Checked("lan");
        candidate.ExternalCommandsEnabled = form.Checked("external"); candidate.Coordinator = form.Checked("coordinator");
        candidate.SharedFilesWorkerId = form.Value("sharedHost");
        return candidate;
    }
    private async Task EditSettingsJson()
    {
        RequireWorkerManagementAllowed("replace the service configuration");
        await RefreshSettings(); var updated = EditJson("Full service settings", settings); if (updated is null) return;
        settings = updated; await SaveSettings();
    }
    private T? EditJson<T>(string title, T value) where T : class
    {
        var form = new FieldsDialog(this, title, "Advanced configuration. Fields are validated by the service.").Add("json", "JSON", Ui.Serialize(value), true);
        return form.ShowDialog() == true ? Ui.Parse<T>(form.Raw("json")) : null;
    }
    private async Task RenameWorker(WorkerRegistration selected)
    {
        var session = await BeginRegistrationEditAsync();
        var worker = session.Settings.Workers.SingleOrDefault(x => x.Id == selected.Id)
            ?? throw new InvalidOperationException("This worker is no longer registered. Refresh the current service.");
        var form = new FieldsDialog(this, "Rename worker",
            "Choose the name shown in machine selectors and model bindings. The connection and worker identity stay the same.")
            .Add("name", "Display name", worker.Name)
            .Add("id", "Worker identity", worker.Id).SetReadOnly("id");
        if (!ShowRegistrationDialog(session, form)) return;
        var name = form.Value("name");
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Enter a worker display name.");
        // Rename only the label in the captured settings revision; never activate a restored
        // connection or replace credentials, endpoints or routing IDs as a side effect.
        worker.Name = name;
        await CommitRegistrationEditAsync(session);
        SetStatus("Worker renamed to " + name + ".");
    }

    private async Task EditWorker(WorkerRegistration original)
    {
        var session = await BeginRegistrationEditAsync();
        original = original.Id.Length == 0 ? new WorkerRegistration() : session.Settings.Workers.SingleOrDefault(x => x.Id == original.Id)
            ?? throw new InvalidOperationException("This worker is no longer registered. Refresh the current service.");
        var form = new FieldsDialog(this, "Worker connection", original.RequiresConfiguration ? "This restored connection is inactive. Review the endpoint and paste a peer token issued by that computer. SSH credentials are optional and independent." : "Use a scoped peer token for shared capacity. Owner access keys are retained only for same-installation administration and legacy recovery.").Add("id", "Worker ID", original.Id).Add("name", "Display name", original.Name).Add("url", "Service URL", original.Url)
            .Secret("peer", "Peer access token (recommended)", original.PeerToken).Secret("key", "Advanced owner access key", original.AccessKey).Add("host", "SSH host (optional)", original.SshHost ?? "").Add("user", "SSH user", original.SshUser ?? "")
            .Add("port", "SSH port", original.SshPort.ToString()).Add("identity", "SSH identity file (optional)", original.SshIdentityFile ?? "")
            .Add("knownHosts", "SSH known-hosts file (recommended)", original.SshKnownHostsFile ?? "").Add("directory", "Remote install directory", original.InstallDirectory ?? "");
        if (original.Id.Length > 0) form.SetReadOnly("id");
        if (!ShowRegistrationDialog(session, form)) return;
        if (original.Id.Length > 0 && form.Value("id") != original.Id) throw new ArgumentException("Keep the existing worker ID.");
        var updated = new WorkerRegistration { Id = form.Value("id"), Name = form.Value("name"), Url = form.Value("url"), PeerToken = form.SecretValue("peer"), AccessKey = form.SecretValue("key"),
            SshHost = form.Value("host"), SshUser = form.Value("user"), SshPort = form.Number("port", 1, 65535), SshIdentityFile = form.Value("identity"),
            SshKnownHostsFile = form.Value("knownHosts"), InstallDirectory = form.Value("directory"), RequiresConfiguration = false };
        if (original.RequiresConfiguration)
        {
            PortableRegistrationPolicy.Validate(new ConfigurationBackupWorker(updated.Id, updated.Name, updated.Url,
                updated.SshHost, updated.SshUser, updated.SshPort, updated.InstallDirectory));
            if (!Uri.TryCreate(updated.Url, UriKind.Absolute, out var endpoint) || endpoint.Scheme is not ("http" or "https"))
                throw new ArgumentException("Specify the restored worker endpoint before enabling it.");
            if (!endpoint.IsLoopback && string.IsNullOrWhiteSpace(updated.PeerToken) && string.IsNullOrWhiteSpace(updated.AccessKey))
                throw new ArgumentException("Supply a peer access token before enabling this network connection.");
        }
        session.Settings.Workers.RemoveAll(x => x.Id == updated.Id); session.Settings.Workers.Add(updated); await CommitRegistrationEditAsync(session);
    }
    private async Task ProbeWorker()
    {
        var worker = Selected<WorkerRegistration>(workers);
        var result = await Get<WorkerProbeResult>("workers.probe", new { id = worker.Id });
        Ui.ShowText(this, "Worker probe · " + worker.Name, FormatWorkerProbe(result));
    }
    private async Task RemoveWorker() { var selected = Selected<WorkerRegistration>(workers); if (!Confirm($"Remove worker '{selected.Name}' from this configuration?")) return; await RefreshSettings(); settings.Workers.RemoveAll(x => x.Id == selected.Id); await SaveSettings(); }
    private async Task EditExecutable(ExecutableRegistration original)
    {
        var session = await BeginRegistrationEditAsync();
        original = original.Id.Length == 0 ? new ExecutableRegistration() : session.Settings.Executables.SingleOrDefault(x => x.Id == original.Id)
            ?? throw new InvalidOperationException("This executable is no longer registered. Refresh the current service.");
        var form = new FieldsDialog(this, "Registered executable", (original.RequiresConfiguration ? "This restored executable is inactive. Review its path and supply any local prefix arguments. " : "") + "Each argument is one line. Do not enter a shell command string.")
            .Add("id", "Tool ID", original.Id).Add("path", "Executable path", original.Path).Add("arguments", "Prefix arguments", string.Join('\n', original.PrefixArguments), true);
        if (!ShowRegistrationDialog(session, form)) return;
        if (original.Id.Length > 0 && form.Value("id") != original.Id) throw new ArgumentException("Keep the existing tool ID.");
        var updated = new ExecutableRegistration { Id = form.Value("id"), Path = form.Value("path"), PrefixArguments = form.Lines("arguments"), RequiresConfiguration = false };
        if (original.RequiresConfiguration)
            PortableRegistrationPolicy.Validate(new ConfigurationBackupExecutable(updated.Id, updated.Path, updated.PrefixArguments.Count > 0));
        session.Settings.Executables.RemoveAll(x => x.Id == updated.Id); session.Settings.Executables.Add(updated); await CommitRegistrationEditAsync(session);
    }
    private async Task RemoveExecutable() { var item = Selected<ExecutableRegistration>(executables); await RefreshSettings(); settings.Executables.RemoveAll(x => x.Id == item.Id); await SaveSettings(); }
    private async Task EditMcp(McpRegistration original)
    {
        var session = await BeginRegistrationEditAsync();
        original = original.Id.Length == 0 ? new McpRegistration() : session.Settings.McpServers.SingleOrDefault(x => x.Id == original.Id)
            ?? throw new InvalidOperationException("This MCP server is no longer registered. Refresh the current service.");
        if (original.CortexNative is not null || original.CortexNativeRequired)
        {
            await AddInstalledCortex();
            return;
        }
        var form = new FieldsDialog(this, "MCP server", (original.RequiresConfiguration ? "This restored provider is inactive. Review its executable, endpoint and local arguments before enabling it. " : "") + "Use an executable with separate arguments, optionally through the user-session connector.")
            .Add("id", "Server ID", original.Id).Add("executable", "Executable", original.Executable).Add("args", "Arguments, one per line", string.Join('\n', original.Arguments), true)
            .Add("url", "HTTP URL (optional)", original.Url ?? "").Check("user", "Connect through the user-session helper", original.ViaUserConnector).Check("enabled", "Enabled", original.Enabled);
        if (!ShowRegistrationDialog(session, form)) return;
        if (original.Id.Length > 0 && form.Value("id") != original.Id) throw new ArgumentException("Keep the existing server ID.");
        var updated = new McpRegistration { Id = form.Value("id"), Executable = form.Value("executable"), Arguments = form.Lines("args"),
            Url = string.IsNullOrEmpty(form.Value("url")) ? null : form.Value("url"), ViaUserConnector = form.Checked("user"), Enabled = form.Checked("enabled"), RequiresConfiguration = false };
        if (original.RequiresConfiguration)
        {
            PortableRegistrationPolicy.Validate(new ConfigurationBackupMcp(updated.Id, updated.Executable, updated.Url,
                updated.ViaUserConnector, updated.Enabled, updated.Arguments.Count > 0));
            if (updated.ViaUserConnector && updated.Url is null)
                throw new ArgumentException("Specify the local connector endpoint before enabling this restored provider.");
        }
        session.Settings.McpServers.RemoveAll(x => x.Id == updated.Id); session.Settings.McpServers.Add(updated); await CommitRegistrationEditAsync(session);
    }
    private async Task RemoveMcp() { var item = Selected<McpRegistration>(mcp); await RefreshSettings(); settings.McpServers.RemoveAll(x => x.Id == item.Id); await SaveSettings(); }
    private async Task EditBackground(BackgroundTaskDefinition original)
    {
        await RefreshSettings();
        var profiles = await Get<List<AgentProfile>>("agents.list");
        var profileChoices = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var profile in profiles.OrderBy(x => x.Role).ThenBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            var basic = $"{profile.Name} · {FriendlyAgentRole(profile.Role)}" + (string.IsNullOrWhiteSpace(profile.Purpose) ? "" : " · " + profile.Purpose);
            var label = basic;
            for (var suffix = 2; profileChoices.ContainsKey(label); suffix++) label = $"{basic} ({suffix})";
            profileChoices[label] = profile.Id;
        }
        if (profileChoices.Count == 0) profileChoices["No agents are configured"] = "";
        var selected = profileChoices.FirstOrDefault(x => x.Value == original.ProfileId).Key ?? profileChoices.Keys.First();
        var createAgent = false;
        var form = new FieldsDialog(this, "Background checklist", "Choose a configured agent by name. Only enabled checklists run, and only when background work is enabled for the service.")
            .Add("name", "Checklist name", original.Name).Choice("profile", "Agent", profileChoices.Keys, selected)
            .ActionButton("Create agent…", dialog => { createAgent = true; dialog.Close(); })
            .Add("prompt", "Checklist / instructions", original.Prompt, true)
            .Add("interval", "Interval minutes", original.IntervalMinutes.ToString()).Add("max", "Maximum run minutes", original.MaxRunMinutes.ToString()).Check("enabled", "Enable this checklist", original.Enabled);
        if (form.ShowDialog() != true)
        {
            if (createAgent)
            {
                resumeBackgroundSetup = true; SelectTab("Agents");
                SetStatus("Create or set up an agent. After it is saved, checklist setup will reopen with the new agent available.");
            }
            return;
        }
        var profileId = profileChoices[form.Selection("profile")];
        if (string.IsNullOrWhiteSpace(profileId)) throw new InvalidOperationException("Create an agent before saving this checklist.");
        var updated = Ui.Parse<BackgroundTaskDefinition>(Ui.Serialize(original)); updated.Name = form.Value("name"); updated.ProfileId = profileId; updated.Prompt = form.Raw("prompt");
        updated.IntervalMinutes = form.Number("interval", 1, 525600); updated.MaxRunMinutes = form.Number("max", 1, 1440); updated.Enabled = form.Checked("enabled");
        settings.BackgroundTasks.RemoveAll(x => x.Id == updated.Id); settings.BackgroundTasks.Add(updated); await SaveSettings();
    }
    private static string FriendlyAgentRole(string role) => role switch { "lead" => "Cerebral", "helper" => "Helper", "compactor" => "Context compactor", _ => "Agent" };
    private void SelectTab(string title)
    {
        _ = SelectTab(Tabs, title);
    }
    private static bool SelectTab(TabControl tabs, string title)
    {
        foreach (var tab in tabs.Items.OfType<TabItem>())
        {
            if (string.Equals(tab.Header as string, title, StringComparison.Ordinal) ||
                System.Windows.Automation.AutomationProperties.GetAutomationId(tab) is var id &&
                (id == "ConfigurationTab" + title || id == "ConfigurationArea" + title))
            {
                tabs.SelectedItem = tab;
                return true;
            }
            if (tab.Content is TabControl nested && SelectTab(nested, title))
            {
                tabs.SelectedItem = tab;
                return true;
            }
        }
        return false;
    }
    private async Task ResumeBackgroundSetupIfRequested()
    {
        if (!resumeBackgroundSetup) return;
        resumeBackgroundSetup = false; SelectTab("Background");
        await EditBackground(new BackgroundTaskDefinition());
    }
    private async Task RemoveBackground() { var item = Selected<BackgroundTaskDefinition>(background); await RefreshSettings(); settings.BackgroundTasks.RemoveAll(x => x.Id == item.Id); await SaveSettings(); }
    private async Task ToggleBackground() { await RefreshSettings(); settings.BackgroundTasksEnabled = !settings.BackgroundTasksEnabled; await SaveSettings(); }



    private async Task Inspect(string command) { diagnostics.Text = (await Command(command)).Data.ToString(); }
    private async Task AdvancedCommand()
    {
        var form = new FieldsDialog(this, "Structured command", "Use an operation and JSON arguments. This console has the same local-owner access as the GUI.")
            .Add("operation", "Operation", "health").Add("arguments", "JSON arguments", "{}", true);
        if (form.ShowDialog() != true) return; var result = await Command(form.Value("operation"), Ui.Parse<JsonElement>(form.Raw("arguments")));
        diagnostics.Text = result.Data.ToString(); SetStatus("Command completed.");
    }
    private Task StartLocalWorker() => RetryLocalConnectionAsync();

    private async Task StopLocalWorker()
    {
        RequireWorkerManagementAllowed("stop the Worker");
        if (ServiceConnection.Transport == "pipe")
            await LocalDevelopment.StopPipeAsync(ServiceConnection.LocalEndpoint!, Lifetime.Token, ServiceConnection.LocalDataDirectory);
        else if (ServiceConnection.Transport == "installed-user-pipe")
            await LocalDevelopment.StopInstalledUserPipeAsync(Lifetime.Token);
        else if (ServiceConnection.Transport == "installed-pipe")
            await ManagementProcess.RunAsync(["stop"], Lifetime.Token);
        else await LocalDevelopment.StopAsync(ServiceUrl, Lifetime.Token);
        SetStatus("Local worker is stopping. Saved conversations and models remain available for the next start.");
    }
    private async Task Manage(string action)
    {
        RequireWorkerManagementAllowed("manage the installed service");
        SetStatus($"Service {action}…"); var result = await ManagementProcess.RunAsync([action], Lifetime.Token);
        overview.Text = result; SetStatus($"Service {action} returned. Use Connect to refresh the service connection.");
    }
    private async Task Install()
    {
        RequireWorkerManagementAllowed("install or update Cerebral");
        var picker = new OpenFileDialog { Title = "Choose the Cerebral NSIS installer", Filter = "Installer (*.exe)|*.exe", CheckFileExists = true };
        if (picker.ShowDialog(this) != true) return;
        if (!Confirm("Launch this installer and close Pegasus Cerebral Configuration so its program files can be updated? The installer will request elevation if needed. Close Pegasus Cerebral too before updating it.")) return;
        await ManagementProcess.RunAsync(["deploy", "--installer", picker.FileName], Lifetime.Token);
        // Release our own program files immediately; installer progress belongs to NSIS.
        Application.Current.Shutdown();
    }
    private async Task RemoteManagement()
    {
        RequireWorkerManagementAllowed("manage a remote Worker");
        var worker = Selected<WorkerRegistration>(workers); var form = new FieldsDialog(this, "SSH worker management", "Uses the configured SSH connection and the independent worker management helper.")
            .Add("action", "Action: status, start, stop, or deploy", "status").Add("source", "Linux ARM64 Worker deployment folder (deploy only)", "").Check("sudo", "Use sudo on the remote machine", false);
        if (form.ShowDialog() != true) return;
        var action = form.Value("action"); if (action is not ("status" or "start" or "stop" or "deploy")) throw new ArgumentException("Choose status, start, stop, or deploy.");
        var temporary = Path.Combine(Path.GetTempPath(), "cerebral-worker-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            // Lifecycle tools do not need the API access key in their temporary SSH profile.
            var profile = Ui.Parse<WorkerRegistration>(Ui.Serialize(worker)); profile.AccessKey = "";
            await File.WriteAllTextAsync(temporary, Ui.Serialize(profile), Lifetime.Token);
            var args = new List<string> { "remote", "--profile", temporary, "--action", action };
            if (action == "deploy") { args.Add("--source"); args.Add(form.Value("source")); }
            if (form.Checked("sudo")) args.Add("--sudo");
            var result = await ManagementProcess.RunAsync(args, Lifetime.Token); Ui.ShowText(this, "SSH management", result); SetStatus("Remote management returned.");
        }
        finally { File.Delete(temporary); }
    }
}

