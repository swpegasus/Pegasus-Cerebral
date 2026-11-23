using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;

namespace Cerebral.Desktop;

/// <summary>A passive Windows-style table. Details open only on an explicit native inspection action.</summary>
public sealed class ActivityTimelineView : Grid
{
    public const int PageSize = 100;
    private readonly ComboBox scope = new() { ItemsSource = new[] { "All linked work", "Current task", "All workers", "Selected turn" }, SelectedIndex = 0 };
    private readonly ComboBox agents = new() { DisplayMemberPath = nameof(AgentChoice.Label), SelectedValuePath = nameof(AgentChoice.Coordinate) };
    private readonly ComboBox tools = new();
    private readonly ComboBox states = new();
    private readonly ComboBox operations = new();
    private readonly ComboBox runs = new() { DisplayMemberPath = nameof(RunChoice.Label) };
    private readonly ComboBox interval = new() { ItemsSource = new[] { "All loaded time", "Last 15 minutes", "Last hour", "Last 24 hours" }, SelectedIndex = 0 };
    private readonly TextBox search = new() { MaxLength = 512, MinWidth = 140, ToolTip = "Search this chat's recorded activity" };
    private sealed record RunChoice(string Label, string? Id);
    private sealed record AgentChoice(string Label, string? Coordinate, bool All = false);
    private readonly ListView list = new() { BorderThickness = new Thickness(0), HorizontalContentAlignment = HorizontalAlignment.Stretch };
    private readonly ObservableCollection<Row> visible = [];
    private readonly Dictionary<string, Row> rows = new(StringComparer.Ordinal);
    private readonly TextBlock coverage = Ui.Text("No activity supplied.", 11, Ui.Muted);
    private readonly Button more;
    private readonly Button earlier;
    private readonly Button inspect;
    private readonly Dictionary<Window, DetailState> details = new();
    private readonly HashSet<string> suppliedOwners = new(StringComparer.Ordinal);
    private const int MaximumDetailWindows = 8;
    private ActivityTimelineSnapshot snapshot = new([], false);
    private string? currentConversationId;
    private string? currentTaskId;
    private string? selectedKey;
    private int pageCount = PageSize;
    private bool updating;

    public event EventHandler? LoadEarlierRequested;
    public ActivityTurnFocus? FocusedTurn { get; private set; }
    public long FilterVersion { get; private set; }
    public string? SelectedMemberCoordinate => (agents.SelectedItem as AgentChoice) is { All: false } choice ? choice.Coordinate : null;

    /// <summary>Only an explicit summary activation sets this exact owner/chat/journal-turn filter.</summary>
    public void FocusTurn(ActivityTurnFocus focus)
    {
        Dispatcher.VerifyAccess(); ArgumentNullException.ThrowIfNull(focus);
        if (new[] { focus.OwnerIdentity, focus.ConversationId, focus.RunId }.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("An exact activity owner, chat and turn are required.", nameof(focus));
        if (focus.FirstSequence is < 1 || focus.LastSequence is < 1 ||
            focus.FirstSequence is { } first && focus.LastSequence is { } last && first > last)
            throw new ArgumentException("The activity interval is invalid.", nameof(focus));
        FocusedTurn = focus; selectedKey = null; pageCount = PageSize; FilterVersion++;
        updating = true;
        try
        {
            scope.SelectedItem = "Selected turn";
            foreach (var filter in new[] { tools, states, operations }) filter.SelectedItem = "All";
            agents.SelectedIndex = 0;
            runs.SelectedIndex = 0; interval.SelectedIndex = 0; search.Clear();
        }
        finally { updating = false; }
        Reconcile();
    }
    public ActivityTimelineItem? SelectedItem => selectedKey is null ? null : snapshot.Items.FirstOrDefault(item => item.Key == selectedKey);

    public ActivityTimelineView()
    {
        inspect = Ui.Button("Inspect selected", OpenSelectedDetail);
        RowDefinitions.Add(new() { Height = GridLength.Auto });
        RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star), MinHeight = 70 });
        RowDefinitions.Add(new() { Height = GridLength.Auto });
        var filters = new WrapPanel();
        AddFilter(filters, "Work", scope, "ActivityScope"); AddFilter(filters, "Agent", agents, "ActivityAgentFilter");
        AddFilter(filters, "Tool / route", tools, "ActivityToolFilter"); AddFilter(filters, "State", states, "ActivityStateFilter");
        AddFilter(filters, "Operation", operations, "ActivityOperationFilter");
        AddFilter(filters, "Run", runs, "ActivityRunFilter"); AddFilter(filters, "Time", interval, "ActivityIntervalFilter");
        AutomationProperties.SetAutomationId(search, "ActivitySearch"); AutomationProperties.SetName(search, "Search selected chat activity");
        filters.Children.Add(search);
        Children.Add(filters);
        foreach (var filter in new[] { scope, agents, tools, states, operations, runs, interval })
            filter.SelectionChanged += (_, _) =>
            {
                if (updating) return;
                if (scope.SelectedItem is not "Selected turn") FocusedTurn = null;
                pageCount = PageSize; selectedKey = null; FilterVersion++; Reconcile();
            };
        search.TextChanged += (_, _) => { if (!updating) { pageCount = PageSize; selectedKey = null; FilterVersion++; Reconcile(); } };
        var table = new GridView { AllowsColumnReorder = false };
        Column(table, "Time / state", nameof(Row.TimeState), 160);
        Column(table, "Agent", nameof(Row.Agent), 190, nameof(Row.AgentDisplayTooltip));
        Column(table, "Operation type", nameof(Row.OperationType), 130);
        Column(table, "Tool / route", nameof(Row.Tool), 200);
        // These must remain the final two columns, in this exact order.
        Column(table, "File name", nameof(Row.FileName), 190);
        Column(table, "File path", nameof(Row.FilePath), 390);
        list.View = table;
        var rowStyle = Ui.ExtendStyle<ListViewItem>();
        rowStyle.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch));
        rowStyle.Setters.Add(new Setter(FrameworkElement.MinHeightProperty, 26d));
        list.ItemContainerStyle = rowStyle;
        VirtualizingPanel.SetIsVirtualizing(list, true);
        VirtualizingPanel.SetVirtualizationMode(list, VirtualizationMode.Recycling);
        ScrollViewer.SetCanContentScroll(list, true);
        ScrollViewer.SetHorizontalScrollBarVisibility(list, ScrollBarVisibility.Auto);
        AutomationProperties.SetAutomationId(list, "ActivityTimelineRows");
        AutomationProperties.SetName(list, "Selected chat activity: time and state, agent, operation type, tool route, file name, file path");
        list.PreviewKeyDown += (_, args) =>
        {
            if (args.Key == Key.Enter) { OpenSelectedDetail(); args.Handled = true; return; }
            if (args.Key != Key.C || (Keyboard.Modifiers & ModifierKeys.Control) == 0 || list.SelectedItem is not Row row) return;
            try { Clipboard.SetText(row.Item.RowText); args.Handled = true; }
            catch (System.Runtime.InteropServices.ExternalException) { coverage.Text = "Clipboard is unavailable; full values remain in cell tooltips and the separate Inspect details window."; }
        };
        list.MouseDoubleClick += (_, args) =>
        {
            if (args.OriginalSource is DependencyObject source && ItemsControl.ContainerFromElement(list, source) is ListViewItem { IsSelected: true })
            { OpenSelectedDetail(); args.Handled = true; }
        };
        list.ItemsSource = visible;
        list.SelectionChanged += (_, _) =>
        {
            if (updating) return;
            selectedKey = (list.SelectedItem as Row)?.Item.Key;
            inspect.IsEnabled = SelectedItem is not null;
        };
        Grid.SetRow(list, 1); Children.Add(list);
        more = Ui.Button("Show next 100", () => { pageCount += PageSize; Reconcile(); });
        earlier = Ui.Button("Load more from source", () => LoadEarlierRequested?.Invoke(this, EventArgs.Empty));
        // Font-derived desired height can be below 20 DIPs; keep explicit actions usable in a short dock.
        foreach (var action in new[] { inspect, more, earlier }) action.MinHeight = 26;
        AutomationProperties.SetAutomationId(inspect, "ActivityInspectSelected");
        AutomationProperties.SetAutomationId(more, "ActivityNextPage");
        AutomationProperties.SetAutomationId(earlier, "ActivityLoadEarlier");
        coverage.TextWrapping = TextWrapping.Wrap; coverage.MaxWidth = 600;
        var paging = Ui.Bar(inspect, more, earlier, coverage); Grid.SetRow(paging, 2); Children.Add(paging);
        Update(new([], false), null, null);
    }

    /// <summary>The host supplies only already authorized, redacted records and truthful coverage metadata.</summary>
    public void Update(ActivityTimelineSnapshot value, string? conversationId, string? taskId)
    {
        Dispatcher.VerifyAccess(); ArgumentNullException.ThrowIfNull(value);
        if (currentConversationId != conversationId) InvalidateOwner();
        if (value.Items is null || value.Items.Count > ActivityTimelineSnapshot.MaximumItems || value.OmittedCount < 0 ||
            value.Members is null || value.Members.Count > 65 || value.Members.Any(member => member is null ||
                string.IsNullOrWhiteSpace(member.Coordinate) || member.Coordinate.Length > 1024 ||
                string.IsNullOrWhiteSpace(member.Label) || member.Label.Length > 512 || member.Label.Any(char.IsControl)) ||
            value.Members.Select(member => member.Coordinate).Distinct(StringComparer.Ordinal).Count() != value.Members.Count ||
            value.TotalCount is < 0 || value.PrunedCount is < 0 ||
            value.Items.Any(item => item is null || string.IsNullOrWhiteSpace(item.Key) || item.Key.Length > 2048 ||
                string.IsNullOrWhiteSpace(item.OwnerIdentity) || item.OwnerIdentity.Length > 1024 ||
                item.RequestedAction is null || item.Outcome is null || item.Evidence is null ||
                item.RequestedAction.Length > 16384 || item.Outcome.Length > 16384 || item.Evidence.Length > 16384) ||
            value.Items.Select(item => item.Key).Distinct(StringComparer.Ordinal).Count() != value.Items.Count)
            throw new ArgumentException("Activity requires unique bounded exact identities and bounded approved evidence.", nameof(value));
        // Filtering can only narrow an explicitly supplied Main root, including
        // an empty new root. A name, selected worker or task is never that root.
        value = value with { Items = value.Items.Where(item => BelongsToMain(item, conversationId)).ToArray(),
            Members = value.Members.Where(member => member.OwningMainConversationId == conversationId).ToArray() };
        // A different owner cannot inherit selected evidence or read position from the old dataset.
        var owners = value.Items.Select(item => item.OwnerIdentity).ToHashSet(StringComparer.Ordinal);
        // An empty loaded window says nothing about owner identity; retained details remain reviewable.
        if (owners.Count > 0 && suppliedOwners.Count > 0 && !suppliedOwners.SetEquals(owners)) InvalidateOwner();
        if (owners.Count > 0) suppliedOwners.UnionWith(owners);
        foreach (var item in value.Items)
            if (rows.TryGetValue(item.Key, out var prior) &&
                (prior.Item.OwnerIdentity != item.OwnerIdentity || prior.Item.ConversationId != item.ConversationId ||
                  prior.Item.RunId != item.RunId || prior.Item.TaskId != item.TaskId ||
                  prior.Item.OwningMainConversationId != item.OwningMainConversationId || prior.Item.MemberCoordinate != item.MemberCoordinate))
                throw new ArgumentException("An activity key cannot be retargeted to different owner/chat/task/run coordinates.", nameof(value));
        if (scope.SelectedItem is "Current task" && taskId != currentTaskId)
            selectedKey = null;
        snapshot = value; currentConversationId = conversationId; currentTaskId = taskId;
        foreach (var detail in details.Values)
        {
            var latest = value.Items.FirstOrDefault(item => item.Key == detail.Item.Key);
            detail.Notice.Text = latest is null
                ? "Retained snapshot: this operation is outside the currently loaded window."
                : latest.Outcome != detail.Item.Outcome || !latest.EventIds.SequenceEqual(detail.Item.EventIds)
                    ? "Retained snapshot: newer evidence is available in the table; reopen the row to inspect it."
                    : "Snapshot of supplied recorded evidence; it does not update or execute work.";
        }
        updating = true;
        try
        {
            var selectedAgent = agents.SelectedItem as AgentChoice;
            var rowChoices = value.Items
                .GroupBy(item => item.MemberCoordinate, StringComparer.Ordinal)
                .Select(group => new AgentChoice(group.Key is null ? "Agent not recorded" :
                    group.OrderByDescending(item => item.At).First().AgentDisplay, group.Key));
            var agentChoices = new[] { new AgentChoice("All", null, All: true) }.Concat(value.Members
                .Select(member => new AgentChoice(member.Label, member.Coordinate)).Concat(rowChoices)
                .DistinctBy(choice => choice.Coordinate).OrderBy(item => item.Label, StringComparer.Ordinal)).ToArray();
            agents.ItemsSource = agentChoices;
            agents.SelectedItem = agentChoices.FirstOrDefault(item => selectedAgent is not null &&
                item.All == selectedAgent.All && item.Coordinate == selectedAgent.Coordinate) ?? agentChoices[0];
            Choices(tools, value.Items.Select(item => item.Tool));
            Choices(states, value.Items.Select(item => item.State));
            Choices(operations, value.Items.Select(item => item.OperationDisplay));
            var selectedRun = (runs.SelectedItem as RunChoice)?.Id;
            var runChoices = new[] { new RunChoice("All runs", null) }.Concat(value.Items
                .GroupBy(item => item.RunId, StringComparer.Ordinal).OrderBy(group => group.Min(item => item.At))
                .Select((group, index) => new RunChoice("Run " + (index + 1), group.Key))).ToArray();
            runs.ItemsSource = runChoices; runs.SelectedItem = runChoices.FirstOrDefault(item => item.Id == selectedRun) ?? runChoices[0];
            foreach (var key in rows.Keys.Except(value.Items.Select(item => item.Key), StringComparer.Ordinal).ToArray()) rows.Remove(key);
            foreach (var item in value.Items)
                if (rows.TryGetValue(item.Key, out var row)) row.Update(item); else rows.Add(item.Key, new(item));
        }
        finally { updating = false; }
        Reconcile();
    }

    private void Reconcile()
    {
        var now = DateTimeOffset.UtcNow;
        var filtered = snapshot.Items.Where(item => BelongsToMain(item, currentConversationId)).Where(item => scope.SelectedItem switch
        {
            "All linked work" => true,
            "Current task" => currentTaskId is not null && item.TaskId == currentTaskId,
            "All workers" => item.Role == ActivityTimelineRole.Worker,
            // RunId is the native journal TurnId. Keep the whole exact turn interval so late
            // receipts remain visible without admitting a neighbouring run or another owner.
            "Selected turn" => FocusedTurn is { } focus && item.OwnerIdentity == focus.OwnerIdentity &&
                item.ConversationId == focus.ConversationId && item.RunId == focus.RunId &&
                (focus.FirstSequence is not { } first || (item.FirstSequence > 0 ? item.FirstSequence : item.Sequence) >= first) &&
                (focus.LastSequence is not { } last || (item.FirstSequence > 0 ? item.FirstSequence : item.Sequence) <= last),
            _ => false
        }).Where(item => (agents.SelectedItem is not AgentChoice agent || agent.All || item.MemberCoordinate == agent.Coordinate) &&
            Matches(tools, item.Tool) && Matches(states, item.State) &&
            Matches(operations, item.OperationDisplay) && ((runs.SelectedItem as RunChoice)?.Id is not { } run || item.RunId == run) &&
            WithinInterval(item.At, interval.SelectedItem as string, now) &&
            (search.Text.Length == 0 || string.Join("\n", item.Agent, item.AgentDisplay, item.OperationDisplay, item.Tool, item.FileName,
                item.FilePath, item.RequestedAction, item.Outcome).Contains(search.Text, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(item => item.At).ThenBy(item => item.Sequence).ThenBy(item => item.Key, StringComparer.Ordinal).ToArray();
        var page = filtered.Take(pageCount).Select(item => rows[item.Key]).ToArray();
        updating = true;
        try
        {
            for (var index = visible.Count - 1; index >= 0; index--) if (!page.Contains(visible[index])) visible.RemoveAt(index);
            for (var index = 0; index < page.Length; index++)
            {
                if (index < visible.Count && ReferenceEquals(visible[index], page[index])) continue;
                var old = visible.IndexOf(page[index]);
                if (old >= 0) visible.Move(old, index); else visible.Insert(index, page[index]);
            }
            list.SelectedItem = page.FirstOrDefault(row => row.Item.Key == selectedKey);
        }
        finally { updating = false; }
        more.Visibility = filtered.Length > page.Length ? Visibility.Visible : Visibility.Collapsed;
        earlier.Visibility = snapshot.HasEarlier || snapshot.OmittedCount > 0 ? Visibility.Visible : Visibility.Collapsed;
        earlier.IsEnabled = LoadEarlierRequested is not null;
        coverage.Text = (scope.SelectedItem is "Selected turn" ? "Selected turn · " : "") +
            $"Showing {page.Length} of {filtered.Length} matching · {snapshot.Items.Count} loaded" +
            $" · total {snapshot.TotalCount?.ToString() ?? "not recorded"}" +
            (snapshot.HasEarlier ? " · earlier/source coverage incomplete" : " · supplied window") +
            $" · prior pruning {snapshot.PrunedCount?.ToString() ?? "not recorded"}" +
            (snapshot.OmittedCount > 0 ? $" · {snapshot.OmittedCount} outside this view window" : "") +
            (snapshot.CoverageNote is { Length: > 0 } note ? " · " + note : "");
        inspect.IsEnabled = SelectedItem is not null;
    }

    internal static bool BelongsToMain(ActivityTimelineItem item, string? mainId) =>
        !string.IsNullOrWhiteSpace(mainId) && item.OwningMainConversationId == mainId;

    internal static bool WithinInterval(DateTimeOffset at, string? selection, DateTimeOffset now) => selection switch
    {
        "Last 15 minutes" => at >= now.AddMinutes(-15) && at <= now,
        "Last hour" => at >= now.AddHours(-1) && at <= now,
        "Last 24 hours" => at >= now.AddHours(-24) && at <= now,
        _ => true
    };

    /// <summary>Call before adopting another authenticated connection, even if its display name is unchanged.</summary>
    public void InvalidateOwner()
    {
        Dispatcher.VerifyAccess();
        foreach (var window in details.Keys.ToArray()) window.Close();
        details.Clear(); suppliedOwners.Clear(); selectedKey = null; rows.Clear(); visible.Clear(); pageCount = PageSize; FilterVersion++;
        currentConversationId = null; currentTaskId = null; FocusedTurn = null;
        var wasUpdating = updating;
        updating = true;
        try
        {
            scope.SelectedIndex = 0;
            foreach (var filter in new[] { tools, states, operations })
            { filter.ItemsSource = new[] { "All" }; filter.SelectedIndex = 0; }
            agents.ItemsSource = new[] { new AgentChoice("All", null, All: true) }; agents.SelectedIndex = 0;
            runs.ItemsSource = new[] { new RunChoice("All runs", null) }; runs.SelectedIndex = 0;
            interval.SelectedIndex = 0; search.Clear();
        }
        finally { updating = wasUpdating; }
        snapshot = new([], true, CoverageNote: "Owner changed; activity must be supplied by the new verified scope.");
        coverage.Text = snapshot.CoverageNote;
        more.Visibility = earlier.Visibility = Visibility.Collapsed;
        inspect.IsEnabled = false;
    }

    private void OpenSelectedDetail()
    {
        if (SelectedItem is not { } item) return;
        if (details.Count >= MaximumDetailWindows)
        { coverage.Text = "Eight detail windows are open. Close one before inspecting another snapshot."; return; }
        var window = CreateDetailForInspection(item);
        if (Window.GetWindow(this) is { } owner) { window.Owner = owner; window.Icon = owner.Icon; }
        window.Show();
    }

    /// <summary>Constructs and binds an unshown immutable snapshot; Show is exclusively in the explicit-action handler.</summary>
    internal Window CreateDetailForInspection(ActivityTimelineItem item)
    {
        Dispatcher.VerifyAccess();
        if (!snapshot.Items.Any(current => current.Key == item.Key && current.OwnerIdentity == item.OwnerIdentity &&
            current.ConversationId == item.ConversationId && current.RunId == item.RunId && current.TaskId == item.TaskId))
            throw new InvalidOperationException("The detail snapshot is outside this activity owner's supplied window.");
        var captured = item with { EventIds = Array.AsReadOnly(item.EventIds.ToArray()) };
        var panel = new StackPanel { Margin = new Thickness(12) };
        panel.Children.Add(Ui.Text(captured.Caption, 15));
        var notice = Ui.Text("Snapshot of supplied recorded evidence; it does not update or execute work.", 12, Ui.Muted);
        panel.Children.Add(notice);
        if (captured.ContextAttribution is not null && captured.AgentDisplay != captured.Agent)
        {
            var context = Detail("ActivityOwnedContext"); context.Text = captured.AgentDisplayTooltip;
            AddDetail(panel, "Owned context (separate from recorded actor)", context);
        }
        panel.Children.Add(Ui.Text(captured.Correlation + " · " + captured.TargetRole, 12, Ui.Muted));
        var requested = Detail("ActivityRequested"); requested.Text = captured.RequestedAction;
        var outcome = Detail("ActivityOutcome"); outcome.Text = captured.Outcome;
        var evidence = Detail("ActivityEvidence"); evidence.Text = captured.Evidence +
            $"\nOwning Main chat ID: {captured.OwningMainConversationId ?? "Not recorded"}\nSource conversation ID: {captured.ConversationId}" +
            $"\nExact key: {captured.Key}\nEvent IDs: {string.Join(", ", captured.EventIds)}";
        AddDetail(panel, "Requested action", requested); AddDetail(panel, "Recorded outcome / diff / output", outcome);
        AddDetail(panel, "Correlation / receipt evidence", evidence);
        var window = new Window { Title = captured.OperationType + " · " + captured.FileName + " · Activity details",
            Width = 900, Height = 620, MinWidth = 480, MinHeight = 300, Tag = captured,
            Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto } };
        AutomationProperties.SetAutomationId(window, "ActivityDetailSnapshot");
        details.Add(window, new(captured, notice));
        window.Closed += (_, _) => details.Remove(window);
        return window;
    }

    private sealed record DetailState(ActivityTimelineItem Item, TextBlock Notice);

    private static bool Matches(ComboBox filter, string value) => filter.SelectedItem is not string chosen || chosen == "All" || chosen == value;
    private static void Choices(ComboBox box, IEnumerable<string> values)
    {
        var selected = box.SelectedItem as string ?? "All";
        box.ItemsSource = new[] { "All" }.Concat(values).Append(selected).Distinct(StringComparer.Ordinal).OrderBy(value => value == "All" ? "" : value, StringComparer.Ordinal).ToArray();
        box.SelectedItem = selected;
    }
    private static TextBox Detail(string id)
    {
        var box = new TextBox { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap,
            Background = Ui.Paper, Foreground = Ui.Ink, BorderThickness = new Thickness(0), Padding = new Thickness(5), MaxHeight = 200,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        AutomationProperties.SetAutomationId(box, id); TextContextMenus.Attach(box); return box;
    }
    private static void AddDetail(Panel panel, string label, TextBox box)
    { panel.Children.Add(Ui.Text(label, 12, Ui.Muted)); panel.Children.Add(box); }
    private static void AddFilter(Panel panel, string label, ComboBox box, string id)
    {
        var pair = new StackPanel { Margin = new Thickness(3) }; box.MinWidth = 110; box.MaxWidth = 230;
        AutomationProperties.SetAutomationId(box, id); AutomationProperties.SetName(box, label);
        pair.Children.Add(Ui.Text(label, 11, Ui.Muted)); pair.Children.Add(box); panel.Children.Add(pair);
    }
    private static void Column(GridView view, string header, string property, double width, string? tooltipProperty = null)
    {
        var cell = new FrameworkElementFactory(typeof(TextBlock));
        cell.SetBinding(TextBlock.TextProperty, new Binding(property));
        cell.SetBinding(ToolTipProperty, new Binding(tooltipProperty ?? property));
        cell.SetBinding(AutomationProperties.NameProperty, new Binding(property));
        cell.SetBinding(AutomationProperties.HelpTextProperty, new Binding(tooltipProperty ?? property));
        cell.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
        cell.SetValue(TextBlock.TextWrappingProperty, TextWrapping.NoWrap);
        cell.SetValue(FrameworkElement.MarginProperty, new Thickness(4, 5, 4, 5));
        view.Columns.Add(new GridViewColumn { Header = header, Width = width, CellTemplate = new DataTemplate { VisualTree = cell } });
    }
    private sealed class Row(ActivityTimelineItem item) : INotifyPropertyChanged
    {
        internal ActivityTimelineItem Item { get; private set; } = item;
        public string Caption => Item.Caption;
        public string TimeState => Item.TimeState;
        public string Agent => Item.AgentDisplay;
        public string AgentDisplayTooltip => Item.AgentDisplayTooltip;
        public string ActorContext => Item.ActorContext;
        public string ActorContextTooltip => Item.ActorContextTooltip;
        public string OperationType => Item.OperationDisplay;
        public string Tool => Item.Tool;
        public string FileName => Item.FileName;
        public string FilePath => Item.FilePath;
        public event PropertyChangedEventHandler? PropertyChanged;
        internal void Update(ActivityTimelineItem value)
        {
            if (Item == value) return;
            Item = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
        }
    }
}
