using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using Cerebral.Core;
using Cerebral.Desktop;

namespace Cerebral.Integrator;

/// <summary>Owner-bound profile draft editor. It never dispatches a permission save.</summary>
internal sealed class ProfilePermissionEditor : Window, IDisposable
{
    private readonly Func<bool> current;
    private readonly Func<CancellationToken, Task<ProfilePermissionCatalog>> readCatalog;
    private readonly Action<ProfilePermissionDraft> apply;
    private readonly CancellationTokenSource lifetime;
    private readonly DispatcherTimer guard = new() { Interval = TimeSpan.FromMilliseconds(400) };
    private ProfilePermissionDraft baseline;
    internal ProfilePermissionDraft Draft { get; }
    internal ObservableCollection<ProfilePermissionChoice> Tools { get; } = [];
    internal ObservableCollection<ProfilePermissionChoice> Capabilities { get; } = [];
    internal TextBox Search { get; } = Ui.Input("");
    internal CheckBox AllTools { get; } = new() { Content = "Allow all tools covered by wildcard (*)" };
    internal TextBlock Status { get; } = Ui.Text("", 12, Ui.Muted);
    internal Button ApplyButton { get; }
    internal Button CancelButton { get; }
    private readonly Button ok;
    private readonly ListView toolList;
    private readonly ListView capabilityList;
    private readonly TextBlock detail = Ui.Text("Select a row to see its description and exact ID.", 12, Ui.Muted);
    private const string HelpText =
        "Tools and capabilities are separate allow lists. A tool needs both allowances. " +
        "An unchecked choice removes its exact profile grant; it does not create a deny rule. " +
        "Wildcard * covers supported tools but does not provide capabilities or the exact file.edit-range opt-in. " +
        "Catalog entries describe choices and never grant permission. Unknown saved IDs remain until you explicitly remove them. " +
        "Current paths, package readiness, machine placement and revocations are checked when work is accepted or executed.";
    private bool loading = true;
    private bool loadedOnce;
    private bool disposed;
    private bool bindingInvalid;
    private bool rendering;
    private string catalogStatus = "Reading the registered permission catalog…";
    private string? actionStatus;
    internal Task PendingLoad { get; private set; } = Task.CompletedTask;
    internal bool Dirty => !Draft.Same(baseline);
    internal bool CanApply => !disposed && !loading && !bindingInvalid && current() && Dirty;

    internal ProfilePermissionEditor(Window? owner, string profileName, IEnumerable<string> tools,
        IEnumerable<string> capabilities, Func<bool> current,
        Func<CancellationToken, Task<ProfilePermissionCatalog>> readCatalog,
        Action<ProfilePermissionDraft> apply, CancellationToken ownerLifetime)
    {
        if (owner is not null) { Owner = owner; Icon = owner.Icon; }
        this.current = current; this.readCatalog = readCatalog; this.apply = apply;
        Draft = new(tools, capabilities); baseline = Draft.Copy();
        lifetime = CancellationTokenSource.CreateLinkedTokenSource(ownerLifetime);
        Title = "Profile permissions · " + profileName;
        Width = 860; Height = 720; MinWidth = 590; MinHeight = 600;
        WindowStartupLocation = owner is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner;
        AutomationProperties.SetAutomationId(this, "ProfilePermissionEditor");
        Background = Ui.Paper; Foreground = Ui.Ink;
        var body = new DockPanel { Margin = new Thickness(20), Background = Ui.Paper };
        var heading = new StackPanel();
        heading.Children.Add(Ui.Text("Permissions for " + profileName, 22));
        heading.Children.Add(Ui.Text("Profile-wide · affects every chat using this agent profile.", 14));
        heading.Children.Add(Ui.Text("Apply changes to the draft, then Save the agent to persist them.", 12, Ui.Muted));
        var searchLabel = Ui.Text("Find a tool or capability", 12, Ui.Muted);
        Search.MaxLength = 160; Search.MinHeight = 32; Search.Height = 32; Search.Margin = new Thickness(0, 4, 0, 10);
        AutomationProperties.SetLabeledBy(Search, searchLabel);
        AutomationProperties.SetAutomationId(Search, "ProfilePermissionSearch");
        AutomationProperties.SetHelpText(Search, "Filter names, descriptions and exact permission IDs. Filtering does not change saved choices.");
        heading.Children.Add(searchLabel); heading.Children.Add(Search);
        DockPanel.SetDock(heading, Dock.Top); body.Children.Add(heading);

        ApplyButton = Ui.Button("Apply to draft", () => ApplyDraft(), true);
        ok = Ui.Button("OK", () => { if (!Dirty || ApplyDraft()) Close(); });
        CancelButton = Ui.Button("Cancel", () => { CancelDraft(); Close(); });
        CancelButton.IsCancel = true; ok.IsDefault = true;
        AutomationProperties.SetAutomationId(ApplyButton, "ProfilePermissionApply");
        AutomationProperties.SetAutomationId(CancelButton, "ProfilePermissionCancel");
        var help = Ui.Button("?", ShowPermissionHelp);
        help.MinWidth = 30; help.Width = 30; help.MinHeight = 30; help.Height = 30; help.VerticalAlignment = VerticalAlignment.Center; help.Padding = new Thickness(0); help.FontSize = 14;
        help.ToolTip = "Permission help (F1)";
        AutomationProperties.SetName(help, "Permission help");
        AutomationProperties.SetHelpText(help, "Show how profile tool, capability and wildcard allowances combine.");
        AutomationProperties.SetAutomationId(help, "ProfilePermissionHelp");
        PreviewKeyDown += (_, args) => { if (args.Key == Key.F1) { ShowPermissionHelp(); args.Handled = true; } };
        var footer = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
        Status.TextWrapping = TextWrapping.Wrap;
        AutomationProperties.SetAutomationId(Status, "ProfilePermissionStatus");
        AutomationProperties.SetLiveSetting(Status, AutomationLiveSetting.Polite);
        footer.Children.Add(Status);
        var footerActions = new DockPanel(); DockPanel.SetDock(help, Dock.Left); footerActions.Children.Add(help);
        var actionButtons = Ui.Bar(ok, CancelButton, ApplyButton); actionButtons.HorizontalAlignment = HorizontalAlignment.Right;
        footerActions.Children.Add(actionButtons); footer.Children.Add(footerActions);
        DockPanel.SetDock(footer, Dock.Bottom); body.Children.Add(footer);

        var details = new Expander { Header = "Description and exact IDs", Content = new ScrollViewer { Content = detail, MaxHeight = 130, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }, IsExpanded = false, Margin = new Thickness(0, 10, 0, 0) };
        detail.TextWrapping = TextWrapping.Wrap; detail.MaxHeight = 130;
        AutomationProperties.SetAutomationId(details, "ProfilePermissionDetails");
        DockPanel.SetDock(details, Dock.Bottom); body.Children.Add(details);
        var tabs = new TabControl();
        var toolsPanel = new DockPanel();
        var wildcardPanel = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };
        AllTools.Margin = new Thickness(0, 5, 0, 6);
        AutomationProperties.SetAutomationId(AllTools, "ProfilePermissionWildcard");
        AutomationProperties.SetHelpText(AllTools, "Exact saved wildcard grant. Capabilities are still required; Edit a file range always requires exact opt-in.");
        wildcardPanel.Children.Add(AllTools);
        wildcardPanel.Children.Add(Ui.Text("When wildcard is on, covered rows inherit it. Turn it off to choose individual tools. " +
            "Existing exact grants are retained.", 12, Ui.Muted));
        DockPanel.SetDock(wildcardPanel, Dock.Top); toolsPanel.Children.Add(wildcardPanel);
        toolList = ChoiceList(Tools, "ProfilePermissionTools"); capabilityList = ChoiceList(Capabilities, "ProfilePermissionCapabilities");
        toolsPanel.Children.Add(toolList);
        tabs.Items.Add(Ui.TabPage("Tools", toolsPanel)); tabs.Items.Add(Ui.TabPage("Capabilities", capabilityList));
        body.Children.Add(tabs); Content = body;
        Search.TextChanged += (_, _) => { CollectionViewSource.GetDefaultView(Tools).Refresh(); CollectionViewSource.GetDefaultView(Capabilities).Refresh(); };
        AllTools.Click += (_, _) =>
        {
            if (rendering || loading || !IsCurrent()) return;
            Draft.SetTool("*", AllTools.IsChecked == true); Refresh();
        };
        Loaded += (_, _) => { if (!loadedOnce) { loadedOnce = true; PendingLoad = LoadCatalogAsync(); } guard.Start(); };
        guard.Tick += (_, _) => Refresh();
        Closed += (_, _) => Dispose();
        WindowSizing.Attach(this);
        ReconcileRows([]);
        Refresh();
    }

    private ListView ChoiceList(ObservableCollection<ProfilePermissionChoice> rows, string id)
    {
        var list = new ListView { ItemsSource = rows, MinHeight = 120, Background = Ui.Surface, Foreground = Ui.Ink, BorderBrush = Ui.Line };
        ScrollViewer.SetHorizontalScrollBarVisibility(list, ScrollBarVisibility.Auto);
        var rowStyle = new Style(typeof(ListViewItem), Application.Current.TryFindResource(typeof(ListViewItem)) as Style);
        rowStyle.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(4, 2, 4, 2)));
        rowStyle.Setters.Add(new Setter(FrameworkElement.MinHeightProperty, 28d));
        list.ItemContainerStyle = rowStyle;
        AutomationProperties.SetAutomationId(list, id);
        AutomationProperties.SetHelpText(list, "Use arrow keys to select. Use Tab and Space to change an allowed checkbox. Exact IDs and descriptions are available below.");
        VirtualizingPanel.SetIsVirtualizing(list, true);
        ScrollViewer.SetCanContentScroll(list, true);
        var headerStyle = new Style(typeof(GridViewColumnHeader), Application.Current.TryFindResource(typeof(GridViewColumnHeader)) as Style);
        headerStyle.Setters.Add(new Setter(Control.BackgroundProperty, Ui.RaisedSurface));
        headerStyle.Setters.Add(new Setter(Control.ForegroundProperty, Ui.Ink));
        headerStyle.Setters.Add(new Setter(Control.BorderBrushProperty, Ui.Line));
        var columns = new GridView { ColumnHeaderContainerStyle = headerStyle };
        var check = new FrameworkElementFactory(typeof(CheckBox));
        check.SetBinding(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty, new Binding(nameof(ProfilePermissionChoice.Allow)) { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged });
        check.SetBinding(IsEnabledProperty, new Binding(nameof(ProfilePermissionChoice.CanChange)));
        check.SetBinding(AutomationProperties.NameProperty, new Binding(nameof(ProfilePermissionChoice.AutomationName)));
        check.SetBinding(ToolTipProperty, new Binding(nameof(ProfilePermissionChoice.EffectiveText)));
        check.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Center);
        check.SetValue(FrameworkElement.MinHeightProperty, 28d); check.SetValue(FrameworkElement.HeightProperty, 28d);
        check.SetValue(FrameworkElement.MarginProperty, new Thickness(0)); check.SetValue(Control.PaddingProperty, new Thickness(0));
        columns.Columns.Add(new GridViewColumn { Header = "Allow", Width = 65, CellTemplate = new DataTemplate { VisualTree = check } });
        columns.Columns.Add(new GridViewColumn { Header = "Permission", Width = 280, DisplayMemberBinding = new Binding(nameof(ProfilePermissionChoice.DisplayName)) });
        columns.Columns.Add(new GridViewColumn { Header = "Current profile effect", Width = 215, DisplayMemberBinding = new Binding(nameof(ProfilePermissionChoice.EffectiveText)) });
        list.View = columns;
        CollectionViewSource.GetDefaultView(rows).Filter = item => item is ProfilePermissionChoice row &&
            (Search.Text.Length == 0 || row.SearchText.Contains(Search.Text, StringComparison.CurrentCultureIgnoreCase));
        list.SelectionChanged += (_, _) => { if (list.SelectedItem is ProfilePermissionChoice row) detail.Text = row.Details; };
        return list;
    }

    private void ShowPermissionHelp()
    {
        const string title = "Profile permission help";
        foreach (Window window in OwnedWindows)
        {
            if (window.Title != title) continue;
            if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
            window.Activate();
            return;
        }
        // Keep the permission rows available at the minimum window size. Help
        // belongs to this editor and never applies or saves its draft.
        Ui.ShowText(this, title, HelpText, wrap: true);
    }

    internal async Task LoadCatalogAsync()
    {
        if (disposed || !IsCurrent()) return;
        loading = true; Refresh();
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            deadline.CancelAfter(TimeSpan.FromSeconds(30));
            var result = await readCatalog(deadline.Token).WaitAsync(deadline.Token);
            if (disposed || !IsCurrent()) return;
            if (result.Tools is null || result.Tools.Count > 4096 || result.Tools.Any(x =>
                string.IsNullOrWhiteSpace(x.Name) || x.Name.Length > 256 || x.Name.Any(char.IsControl) ||
                string.IsNullOrWhiteSpace(x.Capability) || x.Capability.Length > 256 || x.Capability.Any(char.IsControl) ||
                x.Description is null || x.Description.Length > 40000) ||
                result.Tools.Select(x => x.Name).Distinct(StringComparer.Ordinal).Count() != result.Tools.Count)
                throw new InvalidOperationException("The catalog is invalid; saved permission IDs were retained.");
            if (result.RegisteredCapabilities is { } caps && (caps.Count > 4096 || caps.Any(x =>
                string.IsNullOrWhiteSpace(x) || x.Length > 256 || x.Any(char.IsControl))))
                throw new InvalidOperationException("The capability catalog is invalid; saved IDs were retained.");
            ReconcileRows(result.Tools, result.RegisteredCapabilities, result.CapabilityDescriptions);
            catalogStatus = result.Complete ? result.Status : result.Status +
                " This is a partial catalog. Saved unknown IDs remain available.";
        }
        catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException and not AccessViolationException)
        {
            if (!disposed) catalogStatus = "Tool catalog unavailable. Saved IDs remain; availability is unknown. " + error.Message[..Math.Min(error.Message.Length, 500)];
        }
        finally { if (!disposed) { loading = false; Refresh(); } }
    }

    private void ReconcileRows(IReadOnlyList<ToolDescriptor> catalog, IReadOnlyList<string>? registeredCaps = null,
        IReadOnlyDictionary<string, string>? capabilityDescriptions = null)
    {
        var byId = catalog.ToDictionary(x => x.Name, StringComparer.Ordinal);
        var toolIds = catalog.Select(x => x.Name).Concat(Draft.Tools.Where(x => x != "*")).Distinct(StringComparer.Ordinal).ToArray();
        var capabilityIds = catalog.Select(x => x.Capability).Concat(registeredCaps ?? []).Concat(Draft.Capabilities).Distinct(StringComparer.Ordinal).ToArray();
        Tools.Clear(); Capabilities.Clear();
        foreach (var id in toolIds.Order(StringComparer.CurrentCultureIgnoreCase))
            Tools.Add(new(Draft, id, false, byId.GetValueOrDefault(id), Refresh));
        var knownCapabilities = catalog.Select(x => x.Capability).Concat(registeredCaps ?? []).ToHashSet(StringComparer.Ordinal);
        foreach (var id in capabilityIds.Order(StringComparer.CurrentCultureIgnoreCase))
            Capabilities.Add(new(Draft, id, true, null, Refresh, knownCapabilities.Contains(id), capabilityDescriptions?.GetValueOrDefault(id)));
    }

    private bool IsCurrent()
    {
        if (!current()) bindingInvalid = true;
        return !bindingInvalid && !disposed;
    }

    internal bool ApplyDraft()
    {
        Refresh();
        if (!CanApply) return false;
        // Parent callback changes only its draft. Its existing Save owns profile CAS and acknowledgement.
        try { apply(Draft.Copy()); }
        catch (Exception error) when (error is InvalidOperationException or ArgumentException)
        {
            actionStatus = "The draft could not be applied. " + error.Message[..Math.Min(error.Message.Length, 500)];
            Refresh(); return false;
        }
        baseline = Draft.Copy();
        actionStatus = "Applied to the profile draft. Select Save in the agent editor to persist these choices.";
        Refresh(); return true;
    }
    internal void CancelDraft()
    {
        Draft.Restore(baseline);
        Refresh();
    }
    internal void Refresh()
    {
        if (disposed) return;
        var valid = IsCurrent();
        rendering = true;
        try
        {
            AllTools.IsChecked = Draft.AllTools;
            AllTools.IsEnabled = valid && !loading;
            toolList.IsEnabled = capabilityList.IsEnabled = valid && !loading;
            foreach (var row in Tools.Concat(Capabilities)) row.Refresh();
            ApplyButton.IsEnabled = CanApply;
            ok.IsEnabled = valid && !loading;
            Status.Text = !valid ? "The service binding changed. This draft was not sent. Close and reopen the profile editor." :
                loading ? "Reading the registered permission catalog…" : catalogStatus +
                (actionStatus is null ? "" : "\n" + actionStatus) + (Dirty ? " Unsaved draft changes." : "");
        }
        finally { rendering = false; }
    }
    public void Dispose()
    {
        if (disposed) return;
        disposed = true; guard.Stop(); lifetime.Cancel(); lifetime.Dispose();
    }
}
