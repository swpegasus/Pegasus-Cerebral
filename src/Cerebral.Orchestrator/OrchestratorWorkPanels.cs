using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using Cerebral.Client;
using Cerebral.Contracts;
using Cerebral.Contracts.Collaboration;
using Cerebral.Contracts.Workflows;
using Cerebral.Desktop;
using Cerebral.Desktop.Plugins;

namespace Cerebral.Orchestrator;

public sealed partial class OrchestratorWindow
{
    private readonly ConversationPanelStackState workPanelState = new();
    private readonly WindowPanelAccordion workPanelAccordion = new();
    private readonly WindowPanelAccordion pluginWorkPanelAccordion = new();
    private Expander? mainWorkPanel, teamWorkPanelGroup, coordinatorWorkPanel, workersWorkPanel, pluginWorkspacePanel;
    private ScrollViewer? pluginPanelViewport;
    private Grid? workPanelLayout;
    private readonly StackPanel teamPanelCards = new();
    private readonly StackPanel pluginWorkPanelCards = new();
    private readonly Dictionary<string, TeamWorkPanel> teamWorkPanels = new(StringComparer.Ordinal);
    private readonly Dictionary<string, PluginWorkPanel> pluginWorkPanels = new(StringComparer.Ordinal);
    private Expander? coordinatorDockPanel, workersDockPanel;
    private long conversationDockPresentationEpoch;
    private bool changingWorkPanels, changingWorkerRows, teamDetailOpening;
    private string? manuallySelectedTeamMainRun, permissionAttentionOwner;
    private readonly HashSet<string> permissionAttentionSeen = new(StringComparer.Ordinal);
    private const string WorkersWorkPanelId = "workers";
    private const string CoordinatorWorkPanelId = "coordinator";
    private ContentControl? coordinatorDockBody;
    private TextBlock? coordinatorDockState, workerDockSummary;
    private ComboBox? coordinatorTaskPicker, workerOrder;
    private CheckBox? workerActiveFirst;
    private DataGrid? workerDockGrid;
    private readonly ObservableCollection<WorkerDockRow> workerDockRows = new();
    private string workerSortMember = nameof(WorkerDockRow.Agent);
    private ListSortDirection workerSortDirection = ListSortDirection.Ascending;

    private sealed record TeamWorkPanel(string TaskId, Expander Panel, Border Header,
        TextBlock Heading, TextBlock State, Button? Popout, ContentControl Body);
    private sealed record PluginWorkPanel(FrameworkElement View, Expander Panel, ContentControl Body)
    {
        internal Window? Popout { get; set; }
    }

    private void NoteCommunicationAttention(ConversationCommunicationPage page)
    {
        if (permissionAttentionOwner != ServiceConnectionIdentity)
        { permissionAttentionSeen.Clear(); permissionAttentionOwner = ServiceConnectionIdentity; }
        var newRequest = false;
        foreach (var permission in page.Permissions)
            if (permission.PendingRequest is { } request && permissionAttentionSeen.Add(request.RequestId)) newRequest = true;
        while (permissionAttentionSeen.Count > 512) permissionAttentionSeen.Remove(permissionAttentionSeen.First());
        if (!newRequest || mainWorkPanel is null) return;
        workPanelState.SetMainExpanded(true); OpenConversationPeer(mainWorkPanel);
        UpdateWorkPanelRows();
    }

    private Expander CreateDockingBar(string title, object body, string id)
    {
        var panel = new Expander { Header = title, Content = body };
        ChatDockAppearance.Configure(panel, workPanelLayout!);
        AutomationProperties.SetAutomationId(panel, id);
        AutomationProperties.SetName(panel, title);
        if (id is "MainWorkPanel" or "CoordinatorWorkPanel" or "WorkersWorkPanel") workPanelAccordion.Register(panel);
        else pluginWorkPanelAccordion.Register(panel);
        return panel;
    }

    private Grid CreateConversationPanelStack(Grid mainBody)
    {
        var root = new Grid { ClipToBounds = true };
        workPanelLayout = root;
        ChatDockAppearance.Apply(root);
        for (var row = 0; row < 6; row++) root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        mainBody.Children.Remove(conversationHeaderStrip);
        mainBody.RowDefinitions[0].Height = new(0);
        var label = Ui.Text("Main", 12); label.Foreground = Ui.Ink;
        label.Margin = new Thickness(0, 0, 10, 0); label.VerticalAlignment = VerticalAlignment.Center;
        var header = new DockPanel(); System.Windows.Controls.DockPanel.SetDock(label, Dock.Left); header.Children.Add(label);
        conversationHeaderStrip!.Margin = new Thickness(0);
        conversationHeaderStrip.Background = Brushes.Transparent;
        sessionHeading.FontSize = 13; sessionHeading.Foreground = Brushes.White;
        sessionState.FontSize = 12; sessionState.Foreground = Ui.Ink;
        if (compactActionsButton is not null) { compactActionsButton.MinHeight = 0; compactActionsButton.Height = 24; compactActionsButton.Padding = new Thickness(6,0,6,0); }
        if (conversationHeaderStrip.Child is Grid title) { title.Height = double.NaN; title.MinHeight = 24; }
        header.Children.Add(conversationHeaderStrip);
        mainWorkPanel = CreateDockingBar("Main", mainBody, "MainWorkPanel");
        mainWorkPanel.Header = header;
        ConfigureFlatConversationDock(mainWorkPanel, "Main");
        mainWorkPanel.SizeChanged += (_, _) => header.Width = Math.Max(0, mainWorkPanel.ActualWidth - 30);
        mainWorkPanel.Expanded += (_, args) =>
        {
            if (!ReferenceEquals(args.OriginalSource, mainWorkPanel)) return;
            if (!changingWorkPanels) { workPanelState.SetMainExpanded(true); teamNavigationVersion++; InvalidateConversationDockPresentation(); }
            UpdateWorkPanelRows(); UpdateDesktopInputGuidanceLayout();
        };
        mainWorkPanel.Collapsed += (_, args) =>
        {
            if (!ReferenceEquals(args.OriginalSource, mainWorkPanel)) return;
            if (!changingWorkPanels) InvalidateConversationDockPresentation();
            InvalidateComposerSuggestion(); UpdateWorkPanelRows();
        };
        root.Children.Add(mainWorkPanel);
        coordinatorDockBody = new ContentControl { Content = Ui.Text("No coordinator is assigned to this chat.", 13, Ui.Muted),
            HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch };
        coordinatorTaskPicker = new ComboBox { MinWidth = 160, DisplayMemberPath = "TaskId",
            SelectedValuePath = "TaskId", HorizontalAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetName(coordinatorTaskPicker, "Selected coordinator task");
        CompactWorkPanelControl(coordinatorTaskPicker);
        coordinatorTaskPicker.SelectionChanged += (_, _) =>
        {
            if (!changingWorkerRows && coordinatorTaskPicker.SelectedValue is string task && task != teamTaskId)
                Run(() => OpenTeamTaskAsync(task));
        };
        var coordinatorBody = new System.Windows.Controls.DockPanel();
        System.Windows.Controls.DockPanel.SetDock(coordinatorTaskPicker, Dock.Top); coordinatorBody.Children.Add(coordinatorTaskPicker);
        coordinatorBody.Children.Add(coordinatorDockBody);
        coordinatorWorkPanel = CreateDockingBar("Coordinator", coordinatorBody, "CoordinatorWorkPanel");
        teamWorkPanelGroup = coordinatorWorkPanel;
        coordinatorDockState = Ui.Text("No task", 12); coordinatorDockState.Foreground = Ui.Muted;
        coordinatorDockState.TextTrimming = TextTrimming.CharacterEllipsis; coordinatorDockState.Margin = new Thickness(0);
        var coordinatorHeader = new Grid(); coordinatorHeader.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); coordinatorHeader.ColumnDefinitions.Add(new());
        var coordinatorLabel = Ui.Text("Coordinator", 12); coordinatorLabel.Foreground = Ui.Ink; coordinatorLabel.Margin = new(0,0,12,0);
        coordinatorHeader.Children.Add(coordinatorLabel); Grid.SetColumn(coordinatorDockState, 1); coordinatorHeader.Children.Add(coordinatorDockState);
        coordinatorWorkPanel.Header = coordinatorHeader;
        var coordinatorMenu = new ContextMenu();
        var popoutCoordinator = new MenuItem { Header = "Pop out details" };
        popoutCoordinator.Click += (_, _) =>
        {
            if (teamGraph is { Complete: true, Coordinator: { } coordinate } graph)
                Run(() => OpenDockRunDetailsAsync(graph.TaskId, coordinate));
        };
        coordinatorMenu.Items.Add(popoutCoordinator); coordinatorWorkPanel.ContextMenu = coordinatorMenu;
        coordinatorWorkPanel.Expanded += (_, args) =>
        {
            if (!ReferenceEquals(args.OriginalSource, coordinatorWorkPanel)) return;
            if (!changingWorkPanels) InvalidateConversationDockPresentation();
            ShowConversationDock(coordinatorWorkPanel);
        };
        coordinatorWorkPanel.Collapsed += ConversationDockCollapsed;
        coordinatorDockPanel = coordinatorWorkPanel;
        ConfigureFlatConversationDock(coordinatorWorkPanel, "Coordinator");
        workersWorkPanel = CreateDockingBar("Workers", CreateWorkersDockBody(), "WorkersWorkPanel");
        workersWorkPanel.Expanded += (_, args) =>
        {
            if (!ReferenceEquals(args.OriginalSource, workersWorkPanel)) return;
            if (!changingWorkPanels) InvalidateConversationDockPresentation();
            ShowConversationDock(workersWorkPanel);
        };
        workersWorkPanel.Collapsed += ConversationDockCollapsed;
        workersDockPanel = workersWorkPanel;
        ConfigureFlatConversationDock(workersWorkPanel, "Workers");
        Grid.SetRow(coordinatorWorkPanel, 1); root.Children.Add(coordinatorWorkPanel);
        Grid.SetRow(workersWorkPanel, 2); root.Children.Add(workersWorkPanel);
        AutomationProperties.SetAutomationId(root, "ConversationDockPanels");
        AutomationProperties.SetName(root, "Main, Coordinator and Workers accordion");
        tasksWorkPanel=CreateDockingBar("Tasks / To-do",CreateTasksDockBody(),"TasksWorkPanel");
        tasksWorkPanel.Expanded+=(_,args)=>
        {
            if(!ReferenceEquals(args.OriginalSource,tasksWorkPanel)) return;
            if(!changingWorkPanels) {workPanelState.SetExpanded(TasksWorkPanelId,true);teamNavigationVersion++;}
            RefreshTaskDockRows();UpdateWorkPanelRows();
        };
        tasksWorkPanel.Collapsed+=(_,_)=>UpdateWorkPanelRows();
        Grid.SetRow(tasksWorkPanel,3);root.Children.Add(tasksWorkPanel);
        var pluginWorkspaces = CreateDockingBar("Plugin workspaces", desktopWorkspaceTrees, "PluginWorkspaceWorkPanel");
        pluginWorkspacePanel = pluginWorkspaces;
        pluginWorkspaces.SetBinding(VisibilityProperty, new Binding(nameof(Visibility)) { Source = desktopWorkspaceTrees });
        pluginWorkspaces.Expanded += (_, _) => UpdateWorkPanelRows();
        pluginWorkspaces.Collapsed += (_, _) => UpdateWorkPanelRows();
        pluginWorkPanelCards.Children.Add(pluginWorkspaces);
        var pluginScroll = new ScrollViewer { Content = pluginWorkPanelCards,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        pluginPanelViewport = pluginScroll;
        Grid.SetRow(pluginScroll, 4); root.Children.Add(pluginScroll);
        ScrollChangedEventHandler scrolled = (_, _) => QueueConversationReadObservation();
        root.AddHandler(ScrollViewer.ScrollChangedEvent, scrolled);
        Closed += (_, _) => root.RemoveHandler(ScrollViewer.ScrollChangedEvent, scrolled);
        DependencyPropertyChangedEventHandler visible = (_, _) => QueueConversationReadObservation();
        root.IsVisibleChanged += visible; Closed += (_, _) => root.IsVisibleChanged -= visible;
        InitializeSharedActivity();
        root.SizeChanged += (_, _) => { UpdateWorkPanelRows(); QueueConversationReadObservation(); };
        Closed += (_, _) => { conversationPeersClosed = true; ClearPluginWorkPanels(); workPanelAccordion.Dispose(); pluginWorkPanelAccordion.Dispose(); };
        if (nativeChat is not null) nativeChat.SnapshotChanged += (_, _) => UpdateStackedTeamPanels(teamGraph);
        OpenConversationPeer(mainWorkPanel);
        UpdateWorkPanelRows();
        return root;
    }

    private UIElement BuildConversationRightRail() => DesktopSidebarView();

    private static void ConfigureFlatConversationDock(Expander panel, string title)
    {
        panel.Margin = new Thickness(0);
        panel.HorizontalContentAlignment = HorizontalAlignment.Stretch;
        panel.VerticalContentAlignment = VerticalAlignment.Stretch;
        var layout = new FrameworkElementFactory(typeof(DockPanel));
        var header = new FrameworkElementFactory(typeof(System.Windows.Controls.Primitives.ToggleButton), "DockHeaderButton");
        header.SetValue(DockPanel.DockProperty, Dock.Top);
        header.SetValue(Control.MinHeightProperty, 24d); header.SetValue(Control.PaddingProperty, new Thickness(6, 1, 6, 1));
        header.SetValue(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch);
        header.SetValue(Control.VerticalContentAlignmentProperty, VerticalAlignment.Center);
        header.SetValue(Control.BackgroundProperty, Ui.Surface); header.SetValue(Control.ForegroundProperty, Ui.Ink);
        header.SetValue(Control.BorderBrushProperty, Ui.Line); header.SetValue(Control.BorderThicknessProperty, new Thickness(0, 0, 0, 1));
        header.SetValue(AutomationProperties.NameProperty, title);
        header.SetBinding(ContentControl.ContentProperty, new Binding(nameof(Expander.Header)) { RelativeSource = RelativeSource.TemplatedParent });
        header.SetBinding(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty,
            new Binding(nameof(Expander.IsExpanded)) { RelativeSource = RelativeSource.TemplatedParent, Mode = BindingMode.TwoWay });
        var frame = new FrameworkElementFactory(typeof(Border), "HeaderFrame");
        foreach (var property in new[] { Border.BackgroundProperty, Border.BorderBrushProperty, Border.BorderThicknessProperty, Border.PaddingProperty })
            frame.SetBinding(property, new Binding(property.Name) { RelativeSource = RelativeSource.TemplatedParent });
        var line = new FrameworkElementFactory(typeof(DockPanel));
        var marker = new FrameworkElementFactory(typeof(TextBlock), "DisclosureMarker");
        marker.SetValue(DockPanel.DockProperty, Dock.Left); marker.SetValue(FrameworkElement.WidthProperty, 14d);
        marker.SetValue(TextBlock.TextProperty, "▸"); marker.SetValue(TextBlock.FontSizeProperty, 11d);
        marker.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        marker.SetBinding(TextBlock.ForegroundProperty, new Binding(nameof(Control.Foreground)) { RelativeSource = RelativeSource.TemplatedParent });
        line.AppendChild(marker);
        var caption = new FrameworkElementFactory(typeof(ContentPresenter));
        caption.SetBinding(ContentPresenter.ContentProperty, new Binding(nameof(ContentControl.Content)) { RelativeSource = RelativeSource.TemplatedParent });
        caption.SetValue(ContentPresenter.RecognizesAccessKeyProperty, true);
        caption.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        caption.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Stretch);
        line.AppendChild(caption); frame.AppendChild(line);
        var headerTemplate = new ControlTemplate(typeof(System.Windows.Controls.Primitives.ToggleButton)) { VisualTree = frame };
        var selected = new Trigger { Property = System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty, Value = true };
        selected.Setters.Add(new Setter(TextBlock.TextProperty, "▾", "DisclosureMarker"));
        selected.Setters.Add(new Setter(Border.BackgroundProperty, Ui.Paper, "HeaderFrame")); headerTemplate.Triggers.Add(selected);
        var focused = new Trigger { Property = UIElement.IsKeyboardFocusedProperty, Value = true };
        focused.Setters.Add(new Setter(Border.BorderBrushProperty, Ui.Accent, "HeaderFrame"));
        focused.Setters.Add(new Setter(Border.BorderThicknessProperty, new Thickness(1), "HeaderFrame")); headerTemplate.Triggers.Add(focused);
        header.SetValue(Control.TemplateProperty, headerTemplate); layout.AppendChild(header);
        var body = new FrameworkElementFactory(typeof(ContentPresenter), "DockBody");
        body.SetBinding(ContentPresenter.ContentProperty, new Binding(nameof(Expander.Content)) { RelativeSource = RelativeSource.TemplatedParent });
        body.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Stretch);
        body.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Stretch);
        layout.AppendChild(body);
        var template = new ControlTemplate(typeof(Expander)) { VisualTree = layout };
        var collapsed = new Trigger { Property = Expander.IsExpandedProperty, Value = false };
        collapsed.Setters.Add(new Setter(UIElement.VisibilityProperty, Visibility.Collapsed, "DockBody"));
        template.Triggers.Add(collapsed); panel.Template = template;
        AutomationProperties.SetName(panel, title);
    }

    private static void CompactWorkPanelControl(Control control)
    {
        control.MinHeight = 24; control.Height = double.NaN;
        control.Padding = new Thickness(Math.Max(6, control.Padding.Left), 1, Math.Max(6, control.Padding.Right), 1);
        control.Margin = new Thickness(control.Margin.Left, 0, control.Margin.Right, 0);
        control.VerticalAlignment = VerticalAlignment.Center; control.VerticalContentAlignment = VerticalAlignment.Center;
    }

    private bool conversationReadObservationQueued, conversationPeersClosed;
    private void QueueConversationReadObservation()
    {
        if (conversationReadObservationQueued || conversationPeersClosed || Dispatcher.HasShutdownStarted) return;
        conversationReadObservationQueued = true;
        // One event-driven callback observes the actual post-template/post-layout viewport.
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, new Action(() =>
        {
            conversationReadObservationQueued = false;
            if (!conversationPeersClosed && !Dispatcher.HasShutdownStarted) ObserveConversationDockReadViewports();
        }));
    }

    private void OpenConversationPeer(Expander panel)
    {
        var prior = changingWorkPanels; changingWorkPanels = true;
        try { workPanelAccordion.Open(panel); }
        finally { changingWorkPanels = prior; }
        UpdateWorkPanelRows(); QueueConversationReadObservation();
    }

    private void InvalidateConversationDockPresentation() => conversationDockPresentationEpoch++;

    private void SetConversationDockVisibility(bool visible)
    {
        activityRequested = visible;
    }

    private void ConversationDockCollapsed(object sender, RoutedEventArgs args)
    {
        if (ReferenceEquals(sender, args.OriginalSource) && !changingWorkPanels) InvalidateConversationDockPresentation();
        QueueConversationReadObservation();
    }

    private void ObserveConversationDockReadViewports()
    {
        foreach (var (runId, presenter) in teamPresenters.ToArray())
            if (teamWorkPanels.TryGetValue(runId, out var panel) && TeamPresenterMounted(panel, presenter))
                presenter.RefreshReadViewportVisibility();
    }

    private void ShowConversationDock(Expander panel) => OpenConversationPeer(panel);

    private sealed record ConversationDockPresentation(object? Coordinator, object? CoordinatorTag, object? WorkerContent,
        WorkerDockRow[] Rows, object? Selected, bool Enabled, BindingBase? CoordinatorStateBinding, string? CoordinatorState);

    private object CaptureConversationDockPresentations() =>
        new ConversationDockPresentation(coordinatorDockBody?.Content, coordinatorDockBody?.Tag, workersDockPanel?.Content,
            workerDockRows.ToArray(), workerDockGrid?.SelectedItem, workerDockGrid?.IsEnabled == true,
            coordinatorDockState is null ? null : BindingOperations.GetBindingBase(coordinatorDockState, TextBlock.TextProperty), coordinatorDockState?.Text);

    private void RestoreConversationDockPresentations(object value)
    {
        var prior = value as ConversationDockPresentation ?? throw new InvalidOperationException("Missing owned dock preimage.");
        if (coordinatorDockBody is not null) { coordinatorDockBody.Content = prior.Coordinator; coordinatorDockBody.Tag = prior.CoordinatorTag; }
        if (workersDockPanel is not null) workersDockPanel.Content = prior.WorkerContent;
        workerDockRows.Clear(); foreach (var row in prior.Rows) workerDockRows.Add(row);
        if (workerDockGrid is not null) { workerDockGrid.SelectedItem = prior.Selected; workerDockGrid.IsEnabled = prior.Enabled; }
        if (coordinatorDockState is not null)
        {
            BindingOperations.ClearBinding(coordinatorDockState, TextBlock.TextProperty);
            if (prior.CoordinatorStateBinding is not null) BindingOperations.SetBinding(coordinatorDockState, TextBlock.TextProperty, prior.CoordinatorStateBinding);
            else coordinatorDockState.Text = prior.CoordinatorState ?? "";
        }
    }

    private void ReconcileConversationDockTask(string? previousTask, string? currentTask)
    {
        if (previousTask != currentTask)
        {
            if (coordinatorDockBody is not null)
            {
                coordinatorDockBody.Content = Ui.Text("Select Coordinator to load its read-only conversation.", 13, Ui.Muted);
                coordinatorDockBody.Tag = null;
            }
            workerDockRows.Clear();
        }
        RenderTeamSwitcher();
    }

    private void UpdateWorkPanelRows()
    {
        if (workPanelLayout is null || mainWorkPanel is null) return;
        var expandedPlugin = pluginWorkspacePanel?.IsExpanded == true || pluginWorkPanels.Values.Any(p => p.Panel.IsExpanded);
        var open = new[] { mainWorkPanel.IsExpanded, coordinatorDockPanel?.IsExpanded == true,
            workersDockPanel?.IsExpanded == true, tasksWorkPanel?.IsExpanded == true, expandedPlugin, false };
        for (var row = 0; row < open.Length; row++)
            workPanelLayout.RowDefinitions[row].Height = open[row] ? new(1, GridUnitType.Star) : GridLength.Auto;
        if (pluginPanelViewport is not null) pluginPanelViewport.MaxHeight = expandedPlugin ? double.PositiveInfinity : 28 * Math.Max(1, pluginWorkPanels.Count + 1);
    }

    private FrameworkElement CreateWorkersDockBody()
    {
        var body = new System.Windows.Controls.DockPanel();
        workerDockSummary = Ui.Text("No verified worker runs in this task.", 12, Ui.Muted);
        workerDockSummary.Margin = new(8,5,8,5);
        System.Windows.Controls.DockPanel.SetDock(workerDockSummary, Dock.Top); body.Children.Add(workerDockSummary);
        workerOrder = new ComboBox { ItemsSource = new[] { "Agent profile", "Completion time" }, SelectedIndex = 0, MinWidth = 145 };
        workerActiveFirst = new CheckBox { Content = "Active first", IsChecked = true, VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetName(workerOrder, "Order worker rows by");
        AutomationProperties.SetAutomationId(workerOrder, "WorkersOrder");
        var options = Ui.Bar(Ui.Text("Order by",12,Ui.Muted), workerOrder, workerActiveFirst,
            Ui.Button("Refresh", () => Run(RefreshTeamForCurrentMainAsync)));
        options.Margin = new Thickness(0);
        foreach (var control in options.Children.OfType<Control>()) CompactWorkPanelControl(control);
        foreach (var label in options.Children.OfType<TextBlock>()) label.Margin = new Thickness(0, 0, 6, 0);
        System.Windows.Controls.DockPanel.SetDock(options, Dock.Top); body.Children.Add(options);
        workerDockGrid = new DataGrid { AutoGenerateColumns = false, IsReadOnly = true, CanUserAddRows = false,
            CanUserDeleteRows = false, CanUserReorderColumns = true, SelectionMode = DataGridSelectionMode.Single,
            SelectionUnit = DataGridSelectionUnit.FullRow, HeadersVisibility = DataGridHeadersVisibility.Column,
            GridLinesVisibility = DataGridGridLinesVisibility.All, RowHeight = 25, FontSize = 13,
            Background = Ui.Surface, Foreground = Brushes.White, BorderThickness = new(0),
            EnableRowVirtualization = true, EnableColumnVirtualization = true, ItemsSource = workerDockRows };
        ScrollViewer.SetHorizontalScrollBarVisibility(workerDockGrid, ScrollBarVisibility.Auto);
        ScrollViewer.SetVerticalScrollBarVisibility(workerDockGrid, ScrollBarVisibility.Auto);
        AutomationProperties.SetAutomationId(workerDockGrid, "WorkersList");
        AutomationProperties.SetName(workerDockGrid, "Worker runs. Double-click or press Enter to pop out read-only details.");
        foreach (var (label,path,width) in new[] {
            ("Agent profile",nameof(WorkerDockRow.Agent),170), ("State",nameof(WorkerDockRow.State),100),
            ("Completion recorded",nameof(WorkerDockRow.CompletionText),170), ("Context ID",nameof(WorkerDockRow.ContextId),300) })
        {
            var textStyle = new Style(typeof(TextBlock));
            textStyle.Setters.Add(new Setter(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis));
            textStyle.Setters.Add(new Setter(FrameworkElement.ToolTipProperty,new Binding(path)));
            workerDockGrid.Columns.Add(new DataGridTextColumn { Header=label, Binding=new Binding(path), MinWidth=width,
                Width=new DataGridLength(width,DataGridLengthUnitType.Pixel), ElementStyle=textStyle,
                SortMemberPath=path==nameof(WorkerDockRow.CompletionText)?nameof(WorkerDockRow.CompletedAt):path });
        }
        workerDockGrid.Sorting += (_, args) =>
        {
            args.Handled=true; workerSortMember=args.Column.SortMemberPath;
            workerSortDirection=args.Column.SortDirection==ListSortDirection.Ascending?ListSortDirection.Descending:ListSortDirection.Ascending;
            foreach(var column in workerDockGrid.Columns) column.SortDirection=null;
            args.Column.SortDirection=workerSortDirection; SortWorkerDockRows();
        };
        workerOrder.SelectionChanged += (_, _) =>
        {
            workerSortMember=workerOrder.SelectedIndex==1?nameof(WorkerDockRow.CompletedAt):nameof(WorkerDockRow.Agent);
            workerSortDirection=workerOrder.SelectedIndex==1?ListSortDirection.Descending:ListSortDirection.Ascending;
            SortWorkerDockRows();
        };
        workerActiveFirst.Checked += (_, _) => SortWorkerDockRows();
        workerActiveFirst.Unchecked += (_, _) => SortWorkerDockRows();
        workerDockGrid.MouseDoubleClick += (_, args) =>
        {
            if (WorkerRowAt(args.OriginalSource as DependencyObject) is { } row)
                Run(() => OpenDockRunDetailsAsync(row.TaskId,row.Coordinate));
        };
        workerDockGrid.PreviewKeyDown += (_, args) =>
        {
            if (args.Key==Key.Enter && workerDockGrid.SelectedItem is WorkerDockRow row)
            { args.Handled=true; Run(() => OpenDockRunDetailsAsync(row.TaskId,row.Coordinate)); }
        };
        workerDockGrid.PreviewMouseRightButtonDown += (_, args) =>
        {
            if (WorkerRowAt(args.OriginalSource as DependencyObject) is { } row) workerDockGrid.SelectedItem=row;
        };
        var menu = new ContextMenu(); var details = new MenuItem { Header="Pop out details" };
        menu.Opened += (_, _) => details.IsEnabled=workerDockGrid.IsEnabled && workerDockGrid.SelectedItem is WorkerDockRow;
        details.Click += (_, _) =>
        {
            if (workerDockGrid.SelectedItem is WorkerDockRow row) Run(() => OpenDockRunDetailsAsync(row.TaskId,row.Coordinate));
        };
        menu.Items.Add(details); workerDockGrid.ContextMenu=menu;
        body.Children.Add(workerDockGrid); return body;
    }

    private static WorkerDockRow? WorkerRowAt(DependencyObject? source)
    {
        for(var node=source; node is not null; node=node is Visual or System.Windows.Media.Media3D.Visual3D
            ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node))
            if(node is DataGridRow { Item: WorkerDockRow row }) return row;
        return null;
    }

    private void SortWorkerDockRows() =>
        WorkerDockProjection.Sort(CollectionViewSource.GetDefaultView(workerDockRows),
            workerActiveFirst?.IsChecked==true,workerSortMember,workerSortDirection);

    private void RefreshCompactTeamNavigation()
    {
        if(coordinatorTaskPicker is null) return;
        changingWorkerRows=true;
        try
        {
            coordinatorTaskPicker.ItemsSource=teamTasks.ToArray();
            coordinatorTaskPicker.SelectedValue=teamTaskId;
            coordinatorTaskPicker.Visibility=teamTasks.Count>1?Visibility.Visible:Visibility.Collapsed;
        }
        finally { changingWorkerRows=false; }
        RefreshWorkerDockRows(); RefreshTaskDockRows();
    }

    private void RefreshWorkerDockRows()
    {
        if(workerDockGrid is null || workerDockSummary is null) return;
        if(!IsConnected || teamGraph is not { Complete:true } graph || graph.SourceConversationId!=durableConversationId || graph.TaskId!=teamTaskId)
        {
            workerDockGrid.IsEnabled=false;
            if(workerDockRows.Any(row=>row.TaskId!=teamTaskId)) workerDockRows.Clear();
            workerDockSummary.Text=workerDockRows.Count==0?"No verified worker runs in this task.":"Worker view unavailable. Last verified rows retained; refresh before opening details.";
            return;
        }
        var selected=(workerDockGrid.SelectedItem as WorkerDockRow)?.RunId;
        var rows=graph.Workers.Select(coordinate =>
        {
            var profile=profiles.FirstOrDefault(p=>p.Id==coordinate.ProfileId);
            var name=profile?.Name is {Length:>0 and <=128} label && !label.Any(char.IsControl)?label:coordinate.ProfileId;
            var snapshot=teamPresenterConnection==ServiceConnectionIdentity && ReferenceEquals(teamPresenterClient,Client) &&
                teamPresenterScopes.TryGetValue(coordinate.RunId,out var scope) && scope.TaskId==graph.TaskId &&
                scope.SourceConversationId==graph.SourceConversationId && teamPresenters.TryGetValue(coordinate.RunId,out var presenter)
                ?presenter.Snapshot:null;
            return new WorkerDockRow(graph.TaskId,coordinate,name,WorkerDockProjection.Completion(coordinate,snapshot));
        }).ToArray();
        ReconcileDockRows(workerDockRows,rows,row=>row.RunId);
        if(selected is not null) workerDockGrid.SelectedItem=workerDockRows.FirstOrDefault(row=>row.RunId==selected);
        workerDockGrid.IsEnabled=true;
        workerDockSummary.Text=$"{rows.Length} worker runs in selected task · {rows.Count(row=>row.Coordinate.Status==WorkflowStatus.Completed)} completed · {rows.Count(row=>row.Coordinate.Status==WorkflowStatus.Failed)} failed"+
            (graph.Synchronized?"":" · task updating")+" · completion time only when recorded in the loaded run";
        SortWorkerDockRows();
    }

    private async Task OpenDockRunDetailsAsync(string taskId, CollaborationViewRun coordinate)
    {
        if(teamDetailOpening || teamGraph is not {Complete:true} graph || graph.TaskId!=taskId ||
            graph.SourceConversationId!=durableConversationId || !SameTeamIdentity(TeamRun(coordinate.RunId),coordinate)) return;
        if(teamPopout is not null)
        {
            if(teamPopoutRunId==coordinate.RunId) teamPopout.Activate();
            else SetStatus("Close the open team details window before opening another worker.");
            return;
        }
        teamDetailOpening=true;
        var owner=Client; var identity=ServiceConnectionIdentity; var navigation=teamNavigationVersion;
        try
        {
            await OpenTeamRunAsync(coordinate,selectPanel:false);
            if(ReferenceEquals(owner,Client) && identity==ServiceConnectionIdentity && navigation==teamNavigationVersion &&
                teamGraph?.TaskId==taskId && teamSelectedRunId==coordinate.RunId && SameTeamIdentity(TeamRun(coordinate.RunId),coordinate))
                PopOutTeamPresenter();
        }
        finally { teamDetailOpening=false; }
    }

    private TeamWorkPanel EnsureTeamWorkPanel(CollaborationViewRun run, string taskId)
    {
        if(teamWorkPanels.TryGetValue(run.RunId,out var existing))
        {
            if(existing.TaskId!=taskId) throw new InvalidOperationException("A run cannot move to another task.");
            return existing;
        }
        var title=Ui.Text(run.ParentRunId is null?"Coordinator":"Worker",13);
        var state=Ui.Text(run.Status.ToString(),12,Ui.Muted);
        var body=new ContentControl { HorizontalContentAlignment=HorizontalAlignment.Stretch,VerticalContentAlignment=VerticalAlignment.Stretch };
        // Retained per-run presenters have no per-run docking bar. Only the selected
        // coordinator body is mounted; worker details mount in the explicit pop-out.
        var dock=run.ParentRunId is null?coordinatorWorkPanel:workersWorkPanel;
        if(dock is null) throw new InvalidOperationException("The chat docking bars are not ready.");
        var item=new TeamWorkPanel(taskId,dock,new Border(),title,state,null,body);
        teamWorkPanels.Add(run.RunId,item); return item;
    }

    private void MountTeamWorkPanel(CollaborationViewRun run, WorkflowWorkspace presenter)
    {
        var taskId=teamGraph?.TaskId??throw new InvalidOperationException("Refresh the accepted task first.");
        var panel=EnsureTeamWorkPanel(run,taskId);
        teamMonitorSurface=panel.Body; teamMonitorHeader=panel.Header;
        teamMonitorHeading=panel.Heading; teamMonitorState=panel.State; teamPopoutButton=null;
        panel.Body.Content=!ReferenceEquals(presenter,teamPopoutPresenter)?presenter:Ui.Text("Open in its separate window.",12,Ui.Muted);
        QueueConversationReadObservation();
        if(run.ParentRunId is null && coordinatorDockBody is not null)
        {
            coordinatorDockBody.Content=panel.Body; coordinatorDockBody.Tag=run.RunId;
            coordinatorDockState?.SetBinding(TextBlock.TextProperty,new Binding(nameof(TextBlock.Text)){Source=panel.State});
        }
        UpdateWorkPanelRows();
    }

    private void UpdateStackedTeamPanels(CollaborationViewGraph? graph)
    {
        if(mainWorkPanel is null || durableConversationId is not {} chatId) return;
        workPanelState.Bind(ServiceConnectionIdentity,chatId);
        ObserveWorkAttentionSnapshot();
        var main=nativeChat?.Snapshot?.Run;
        var mainAttention=main?.Questions is {} pending?"questions:"+pending.Id+":"+pending.Revision:
            main is not null && nativeChat?.Snapshot?.MutationUncertain==true?"main-review:"+main.Id+":"+main.Revision:null;
        var returnedForMain=main is not null && mainAttention is not null && workPanelState.ReturnCurrentMain(main.Id,mainAttention);
        RefreshCompactTeamNavigation();
        if(graph is not {Complete:true,Coordinator:{} coordinator} || graph.SourceConversationId!=chatId)
        { if(returnedForMain) ApplyWorkPanelSelection(); return; }
        EnsureTeamWorkPanel(coordinator,graph.TaskId);
        foreach(var coordinate in new[]{coordinator}.Concat(graph.Workers))
        {
            // Workers are data rows until an explicit detail action loads a presenter.
            if(!teamWorkPanels.TryGetValue(coordinate.RunId,out var panel)) continue;
            panel.Heading.Text=coordinate.ParentRunId is null?"Coordinator · read-only":"Worker · "+RecordedTeamAgentName(coordinate)+" · read-only";
            panel.State.Text=coordinate.Status+(graph.Synchronized?"":" · updating");
        }
        foreach(var stale in teamWorkPanels.Where(pair=>pair.Value.TaskId!=graph.TaskId && !teamPresenters.ContainsKey(pair.Key)).Select(pair=>pair.Key).ToArray())
        { teamWorkPanels[stale].Body.Content=null; teamWorkPanels.Remove(stale); }
        if(coordinatorDockBody is not null && coordinatorDockBody.Tag as string!=coordinator.RunId)
        {
            coordinatorDockBody.Content=Ui.Text("Select Coordinator to load its read-only conversation.",13,Ui.Muted);
            if(coordinatorDockState is not null) { BindingOperations.ClearBinding(coordinatorDockState,TextBlock.TextProperty); coordinatorDockState.Text=coordinator.Status.ToString(); }
        }
        if(main is null) return;
        var activeWork=graph.TaskStatus is CollaborationTaskStatus.Queued or CollaborationTaskStatus.Running or CollaborationTaskStatus.Waiting or CollaborationTaskStatus.Cancelling;
        var followed=workPanelState.AcceptHandoff(ServiceConnectionIdentity,chatId,main.Id,graph.TaskId,graph.SourceRunId,coordinator.RunId,graph.Synchronized&&activeWork,false);
        var attention=main.Questions is {} questions?"questions:"+questions.Id+":"+questions.Revision:
            nativeChat?.Snapshot?.MutationUncertain==true?"main-review:"+main.Id+":"+main.Revision:
            graph.TaskStatus is CollaborationTaskStatus.Completed or CollaborationTaskStatus.Failed or CollaborationTaskStatus.Cancelled or CollaborationTaskStatus.ReconcileRequired?"task:"+graph.TaskStatus:null;
        var returned=attention is not null && graph.SourceRunId is {} sourceRun && workPanelState.ReturnToMain(graph.TaskId,sourceRun,attention);
        if(followed||returned||returnedForMain) ApplyWorkPanelSelection();
        if(followed&&!returned&&workPanelState.IsExpanded(coordinator.RunId)) _=FollowAcceptedCoordinatorAsync(coordinator);
        UpdateWorkPanelRows();
    }

    private async Task FollowAcceptedCoordinatorAsync(CollaborationViewRun coordinator)
    {
        var owner=ServiceConnectionIdentity; var client=Client; var mainId=durableConversationId; var taskId=teamGraph?.TaskId;
        try { await OpenTeamRunAsync(coordinator,selectPanel:false); }
        catch(OperationCanceledException) when(Lifetime.IsCancellationRequested) {}
        catch(Exception error) when(error is not OutOfMemoryException and not StackOverflowException and not AccessViolationException)
        {
            if(!IsConnected||owner!=ServiceConnectionIdentity||!ReferenceEquals(client,Client)||mainId!=durableConversationId||
                teamGraph?.TaskId!=taskId||!SameTeamIdentity(TeamRun(coordinator.RunId),coordinator)) return;
            if(teamWorkPanels.TryGetValue(coordinator.RunId,out var panel)) panel.State.Text="Read-only view unavailable; reopen Coordinator to retry.";
        }
    }

    private string RecordedTeamAgentName(CollaborationViewRun coordinate)
    {
        if(!teamPresenters.TryGetValue(coordinate.RunId,out var presenter)||presenter.Snapshot is not {} snapshot||
            snapshot.RunId!=coordinate.RunId||snapshot.ConversationId!=coordinate.ConversationId) return "Agent not recorded";
        var names=snapshot.Events.Where(item=>item.TurnId==coordinate.RunId&&item.Actor.Id==coordinate.ProfileId)
            .Select(WorkflowWorkspace.RecordedActivityAgent).Where(name=>name!="Agent not recorded").Distinct(StringComparer.Ordinal).Take(2).ToArray();
        return names.Length==1?names[0]:"Agent not recorded";
    }

    private void ApplyWorkPanelSelection()
    {
        var selected=workPanelState.MainExpanded?mainWorkPanel:
            workPanelState.IsExpanded(ActivityWorkPanelId)?sharedActivityPanel:
            workPanelState.IsExpanded(WorkersWorkPanelId)?workersWorkPanel:
            workPanelState.IsExpanded(TasksWorkPanelId)?tasksWorkPanel:
            workPanelState.IsExpanded(CoordinatorWorkPanelId)?coordinatorWorkPanel:
            teamWorkPanels.FirstOrDefault(pair=>workPanelState.IsExpanded(pair.Key)).Value?.Panel;
        if(selected is null) return;
        changingWorkPanels=true;
        try
        {
            if (ReferenceEquals(selected, coordinatorDockPanel) || ReferenceEquals(selected, workersDockPanel)) ShowConversationDock(selected);
            else if (ReferenceEquals(selected, sharedActivityPanel)) OpenEmbeddedActivity();
            else if (ReferenceEquals(selected, mainWorkPanel)) OpenConversationPeer(selected);
            else pluginWorkPanelAccordion.Open(selected);
        }
        finally { changingWorkPanels=false; }
        UpdateWorkPanelRows(); RefreshPageButtons();
    }

    private void ClearStackedTeamPanels()
    {
        foreach(var panel in teamWorkPanels.Values) panel.Body.Content=null;
        teamWorkPanels.Clear(); teamPanelCards.Children.Clear(); workerDockRows.Clear();
        workPanelState.Reset(); manuallySelectedTeamMainRun=null;
        if(coordinatorDockBody is not null) { coordinatorDockBody.Tag=null; coordinatorDockBody.Content=Ui.Text("No coordinator is assigned to this chat.",13,Ui.Muted); }
        if(coordinatorDockState is not null) { BindingOperations.ClearBinding(coordinatorDockState,TextBlock.TextProperty); coordinatorDockState.Text="No task"; }
        if(workerDockSummary is not null) workerDockSummary.Text="No verified worker runs in this task.";
        if(teamSidePane is not null) teamSidePane.Visibility=Visibility.Collapsed;
        if(mainWorkPanel is not null) OpenConversationPeer(mainWorkPanel);
        UpdateWorkPanelRows();
    }

    private void MountPluginWorkPanels()
    {
        var mounts = desktopPlugins?.WorkspacePanels ?? [];
        var retained = new HashSet<string>(StringComparer.Ordinal);
        foreach (var mount in mounts)
        {
            var key = mount.PluginId + ":" + mount.Id; retained.Add(key);
            if (pluginWorkPanels.TryGetValue(key, out var prior) && ReferenceEquals(prior.View, mount.View)) continue;
            RemovePluginWorkPanel(key);
            if (mount.View.Parent is ContentControl old) old.Content = null;
            var body = new ContentControl { Content = mount.View, Height = 260,
                HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch };
            PluginWorkPanel? item = null;
            var popout = Ui.Button("Pop out", () =>
            {
                if (item is null || item.Popout is not null || !pluginWorkPanels.TryGetValue(key, out var current) || !ReferenceEquals(item, current)) return;
                body.Content = null;
                var separate = new ContentControl { Content = mount.View,
                    HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch };
                var window = new Window { Owner = this, Title = mount.Title, Content = separate, Width = 900, Height = 640 };
                item.Popout = window;
                window.Closed += (_, _) =>
                {
                    separate.Content = null; item.Popout = null;
                    if (pluginWorkPanels.TryGetValue(key, out var active) && ReferenceEquals(item, active)) body.Content = mount.View;
                };
                window.Show(); // Only this explicit user action opens a native window.
            });
            var panel = new Expander { Header = Ui.Bar(Ui.Text(mount.Title, 15), popout), Content = body,
                HorizontalContentAlignment = HorizontalAlignment.Stretch, IsExpanded = false };
            if (workPanelLayout is not null) ChatDockAppearance.Configure(panel, workPanelLayout);
            panel.Expanded += (_, _) => UpdateWorkPanelRows();
            panel.Collapsed += (_, _) => UpdateWorkPanelRows();
            pluginWorkPanelAccordion.Register(panel);
            AutomationProperties.SetAutomationId(panel, "PluginWorkPanel-" + key);
            item = new(mount.View, panel, body);
            pluginWorkPanels.Add(key, item); pluginWorkPanelCards.Children.Add(panel);
        }
        foreach (var stale in pluginWorkPanels.Keys.Except(retained, StringComparer.Ordinal).ToArray()) RemovePluginWorkPanel(stale);
    }

    private void RemovePluginWorkPanel(string key)
    {
        if (!pluginWorkPanels.Remove(key, out var item)) return;
        pluginWorkPanelAccordion.Unregister(item.Panel);
        item.Popout?.Close(); item.Body.Content = null; pluginWorkPanelCards.Children.Remove(item.Panel);
    }

    private void ClearPluginWorkPanels()
    {
        foreach (var key in pluginWorkPanels.Keys.ToArray()) RemovePluginWorkPanel(key);
    }
}
