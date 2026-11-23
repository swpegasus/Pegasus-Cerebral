using System.Collections.Specialized;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Input;
using Cerebral.Desktop;

namespace Cerebral.Integrator;

/// <summary>
/// A Settings rail over the existing stable page mounts. TabControls retain selection, preference
/// IDs and programmatic routing; only the rail and the selected detail content are rendered.
/// </summary>
internal sealed class ConfigurationSettingsNavigation : Grid, IDisposable
{
    private readonly TabControl mounts;
    private readonly Dictionary<TabControl, INotifyCollectionChanged> observed = [];
    private readonly Dictionary<TabItem, TreeViewItem> pageNodes = [];
    private readonly Dictionary<TabItem, TreeViewItem> categoryNodes = [];
    private readonly HashSet<string> expandedCategories = new(StringComparer.Ordinal);
    private readonly TreeView navigation = new()
    {
        Background = Ui.Paper, Foreground = Ui.Ink, BorderThickness = new Thickness(0),
        Margin = new Thickness(6, 0, 6, 10)
    };
    private readonly TextBox search = Ui.Input();
    private readonly TextBlock emptyResults = Ui.Text("No matching settings", 13, Ui.Muted);
    private readonly Button clearSearch;
    private double? preferredRailWidth;
    private bool resizingRail;
    private double railDragStartWidth;
    private double? railKeyboardStartWidth;
    internal double? PreferredRailWidth => preferredRailWidth;
    internal event Action? PaneSizeChanged;

    private bool synchronizing;
    private bool disposed;
    private NavigationItem[] visiblePages = [];

    internal TextBox Search => search;
    internal TreeView Navigation => navigation;
    internal IReadOnlyList<NavigationItem> VisiblePages => visiblePages;

    public ConfigurationSettingsNavigation(TabControl mounts)
    {
        this.mounts = mounts;
        AutomationProperties.SetAutomationId(this, "ConfigurationSettingsNavigation");
        AutomationProperties.SetAutomationId(navigation, "ConfigurationSettingsPages");
        AutomationProperties.SetName(navigation, "Settings pages");
        Margin = new Thickness(20, 0, 20, 0);
        ColumnDefinitions.Add(new() { Width = new GridLength(232), MinWidth = 160, MaxWidth = 420 });
        ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        search.MaxLength = 160; search.MinHeight = 36; search.ToolTip = "Search settings";
        AutomationProperties.SetAutomationId(search, "ConfigurationSettingsSearch");
        AutomationProperties.SetName(search, "Search settings");
        clearSearch = Ui.Button("×", () => search.Clear());
        clearSearch.Padding = new Thickness(8, 2, 8, 2); clearSearch.Margin = new Thickness(4, 0, 0, 0);
        clearSearch.MinHeight = 32; clearSearch.ToolTip = "Clear settings search";
        clearSearch.Visibility = Visibility.Collapsed;
        AutomationProperties.SetName(clearSearch, "Clear settings search");
        AutomationProperties.SetAutomationId(clearSearch, "ConfigurationSettingsSearchClear");
        var searchRow = new Grid();
        searchRow.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        searchRow.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        searchRow.Children.Add(search);
        Grid.SetColumn(clearSearch, 1); searchRow.Children.Add(clearSearch);
        var searchPanel = new StackPanel { Margin = new Thickness(12, 14, 12, 14) };
        var searchLabel = Ui.Text("Search settings", 12, Ui.Muted);
        searchLabel.Margin = new Thickness(0, 0, 0, 6);
        searchPanel.Children.Add(searchLabel); searchPanel.Children.Add(searchRow);
        emptyResults.Margin = new Thickness(14, 0, 12, 12);
        emptyResults.Visibility = Visibility.Collapsed;
        var rail = new DockPanel();
        DockPanel.SetDock(searchPanel, Dock.Top); rail.Children.Add(searchPanel);
        DockPanel.SetDock(emptyResults, Dock.Top); rail.Children.Add(emptyResults);
        rail.Children.Add(navigation);
        Children.Add(new Border
        {
            Background = Ui.Paper, BorderBrush = Ui.Line, BorderThickness = new Thickness(0, 0, 1, 0),
            Margin = new Thickness(0, 0, 5, 0), Child = rail
        });
        ScrollViewer.SetHorizontalScrollBarVisibility(navigation, ScrollBarVisibility.Disabled);
        ScrollViewer.SetVerticalScrollBarVisibility(navigation, ScrollBarVisibility.Auto);
        mounts.Margin = new Thickness(0); mounts.Template = ContentOnlyTemplate(); mounts.BorderThickness = new Thickness(0);
        Grid.SetColumn(mounts, 1); Children.Add(mounts);
        // Put the divider inside the rail's existing column, preserving detail-column identity.
        var divider = new GridSplitter
        {
            Width = 5, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Stretch,
            ResizeDirection = GridResizeDirection.Columns, ResizeBehavior = GridResizeBehavior.CurrentAndNext,
            ShowsPreview = true, Focusable = true, Background = Ui.Line, Cursor = Cursors.SizeWE,
            ToolTip = "Drag to resize settings navigation; arrow keys resize when focused."
        };
        AutomationProperties.SetAutomationId(divider, "ConfigurationNavigationDivider");
        AutomationProperties.SetName(divider, "Resize settings navigation");
        divider.DragStarted += (_, _) =>
        {
            railDragStartWidth = ColumnDefinitions[0].Width.Value; resizingRail = true;
        };
        divider.DragCompleted += (_, args) =>
        {
            resizingRail = false;
            if (!args.Canceled) RememberRailWidth(railDragStartWidth);
            InvalidateMeasure();
        };
        // Capture before native KeyDown, then observe its handled result before the next measure.
        divider.PreviewKeyDown += (_, args) =>
        {
            if (!divider.IsDragging && (args.Key is Key.Left or Key.Right))
                railKeyboardStartWidth = ColumnDefinitions[0].Width.Value;
        };
        divider.AddHandler(Keyboard.KeyDownEvent, new KeyEventHandler((_, args) =>
        {
            if (!divider.IsDragging && (args.Key is Key.Left or Key.Right) && railKeyboardStartWidth is { } before)
            {
                railKeyboardStartWidth = null; RememberRailWidth(before);
            }
        }), handledEventsToo: true);
        Panel.SetZIndex(divider, 1); Children.Add(divider);
        navigation.SelectedItemChanged += NavigationSelected;
        search.TextChanged += SearchChanged;
        ObserveMounts(); RebuildNavigation();
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        // Viewport fitting changes display only. Never turn a temporary clamp into the saved preference.
        var available = double.IsFinite(availableSize.Width) ? availableSize.Width : 1120;
        var maximum = Math.Min(420, Math.Max(160, available - 700));
        ColumnDefinitions[0].MaxWidth = maximum;
        if (!resizingRail)
            ColumnDefinitions[0].Width = new GridLength(Math.Clamp(preferredRailWidth ?? (available < 1060 ? 208 : 232), 160, maximum));
        return base.MeasureOverride(availableSize);
    }

    internal void HydratePreferredRailWidth(double? width)
    {
        if (width is { } value && (!double.IsFinite(value) || value is < 160 or > 420))
            throw new ArgumentOutOfRangeException(nameof(width));
        preferredRailWidth = width;
        InvalidateMeasure();
    }

    private void RememberRailWidth(double before)
    {
        var width = ColumnDefinitions[0].Width;
        if (!width.IsAbsolute || !double.IsFinite(width.Value)) return;
        if (Math.Abs(width.Value - before) < .01) return; // A key/drag at a viewport limit is not a new preference.
        var value = Math.Clamp(width.Value, 160, ColumnDefinitions[0].MaxWidth);
        if (preferredRailWidth is { } previous && Math.Abs(previous - value) < .01) return;
        preferredRailWidth = value;
        PaneSizeChanged?.Invoke();
    }

    private void ObserveMounts()
    {
        var wasSynchronizing = synchronizing;
        synchronizing = true;
        try
        {
            var current = mounts.Items.OfType<TabItem>().Select(x => x.Content).OfType<TabControl>().Prepend(mounts).ToHashSet();
            foreach (var removed in observed.Keys.Where(tab => !current.Contains(tab)).ToArray())
            {
                removed.SelectionChanged -= MountSelected; observed[removed].CollectionChanged -= MountItemsChanged; observed.Remove(removed);
            }
            foreach (var tab in current)
            {
                if (!observed.ContainsKey(tab))
                {
                    tab.Template = ContentOnlyTemplate(); tab.BorderThickness = new Thickness(0);
                    var collection = (INotifyCollectionChanged)tab.Items;
                    observed.Add(tab, collection); tab.SelectionChanged += MountSelected; collection.CollectionChanged += MountItemsChanged;
                }
                // The content-only template has no generated header containers to choose a default.
                // Use the first page until normal stable-ID preference hydration chooses another.
                if (tab.SelectedIndex < 0 && tab.Items.Count > 0) tab.SelectedIndex = 0;
            }
        }
        finally { synchronizing = wasSynchronizing; }
    }

    private void MountItemsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (disposed) return;
        ObserveMounts(); RebuildNavigation();
    }

    private void MountSelected(object sender, SelectionChangedEventArgs e)
    {
        if (disposed || synchronizing || !ReferenceEquals(e.OriginalSource, sender)) return;
        // A direct link can leave search results. Expose the destination's actual category and page.
        if (search.Text.Length > 0) search.Clear();
        else SynchronizeSelection();
    }

    private void SearchChanged(object sender, TextChangedEventArgs e)
    {
        if (!disposed && !synchronizing) RebuildNavigation();
    }

    private void NavigationSelected(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (disposed || synchronizing || e.NewValue is not TreeViewItem { Header: NavigationItem item }) return;
        synchronizing = true;
        try
        {
            if (item.Category is null) mounts.SelectedItem = item.Tab;
            else
            {
                mounts.SelectedItem = item.Category;
                if (item.Category.Content is TabControl nested) nested.SelectedItem = item.Tab;
            }
        }
        finally { synchronizing = false; }
        SynchronizeSelection();
    }

    internal bool SelectPage(TabItem page)
    {
        if (!pageNodes.TryGetValue(page, out var node)) return false;
        node.IsSelected = true;
        return true;
    }

    private void RebuildNavigation()
    {
        if (synchronizing || disposed) return;
        synchronizing = true;
        try
        {
            navigation.Items.Clear(); pageNodes.Clear(); categoryNodes.Clear();
            var terms = search.Text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var searching = terms.Length > 0;
            var rows = new List<NavigationItem>();
            foreach (var category in mounts.Items.OfType<TabItem>())
            {
                var matchingPages = (category.Content as TabControl)?.Items.OfType<TabItem>()
                    .Where(page => !searching || terms.All(term => SearchText(category, page).Contains(term, StringComparison.CurrentCultureIgnoreCase)))
                    .ToArray() ?? [];
                if (searching && matchingPages.Length == 0) continue;
                var categoryItem = new NavigationItem(category, null, Label(category), CategoryIcon(category));
                var categoryNode = CreateNode(categoryItem);
                var id = AutomationProperties.GetAutomationId(category);
                categoryNode.FontWeight = FontWeights.SemiBold;
                categoryNode.IsExpanded = searching || expandedCategories.Contains(id) || ReferenceEquals(category, mounts.SelectedItem);
                categoryNode.Expanded += (_, e) =>
                {
                    if (!ReferenceEquals(e.OriginalSource, categoryNode) || search.Text.Length > 0) return;
                    expandedCategories.Add(id);
                };
                categoryNode.Collapsed += (_, e) =>
                {
                    if (!ReferenceEquals(e.OriginalSource, categoryNode) || search.Text.Length > 0) return;
                    expandedCategories.Remove(id);
                };
                categoryNodes.Add(category, categoryNode); navigation.Items.Add(categoryNode);
                foreach (var page in matchingPages)
                {
                    var item = new NavigationItem(page, category, Label(page), "");
                    var node = CreateNode(item);
                    categoryNode.Items.Add(node); pageNodes.Add(page, node); rows.Add(item);
                }
            }
            visiblePages = rows.ToArray();
            emptyResults.Visibility = searching && rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            clearSearch.Visibility = search.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        }
        finally { synchronizing = false; }
        SynchronizeSelection();
    }

    private void SynchronizeSelection()
    {
        if (disposed || synchronizing) return;
        synchronizing = true;
        try
        {
            if (mounts.SelectedItem is not TabItem category) return;
            var page = (category.Content as TabControl)?.SelectedItem as TabItem;
            if (!categoryNodes.TryGetValue(category, out var branch)) return;
            branch.IsExpanded = true;
            var selected = page is not null && pageNodes.TryGetValue(page, out var node) ? node : branch;
            selected.IsSelected = true;
            if (selected.IsLoaded) selected.BringIntoView();
        }
        finally { synchronizing = false; }
    }

    private static TreeViewItem CreateNode(NavigationItem item)
    {
        var node = new TreeViewItem
        {
            Header = item, HeaderTemplate = ItemTemplate(), Style = NavigationItemStyle(), HorizontalContentAlignment = HorizontalAlignment.Stretch
        };
        AutomationProperties.SetAutomationId(node, "SettingsNavigation" + AutomationProperties.GetAutomationId(item.Tab));
        AutomationProperties.SetName(node, item.Label);
        return node;
    }

    private static string Label(TabItem tab) => tab.Header?.ToString() ?? "";
    private static string SearchText(TabItem category, TabItem page)
    {
        // Search short headings/descriptions, never live JSON, model rows or plugin content.
        var heading = (page.Content as DockPanel)?.Children.OfType<StackPanel>().FirstOrDefault();
        var text = heading?.Children.OfType<TextBlock>().Take(2).Select(block => block.Text[..Math.Min(512, block.Text.Length)]) ?? [];
        return string.Join(' ', text.Prepend(Label(page)).Prepend(Label(category)).Append(page.ToolTip as string ?? ""));
    }

    private static string CategoryIcon(TabItem tab) => AutomationProperties.GetAutomationId(tab) switch
    {
        "ConfigurationAreaSystem" => "\uE713", "ConfigurationAreaModelsAndAgents" => "\uE770",
        "ConfigurationAreaToolsExtensionsAndSkills" => "\uE74C", "ConfigurationAreaDataAndDiagnostics" => "\uE9D9",
        "ConfigurationAreaConnections" => "\uE968", "ConfigurationAreaAdvanced" => "\uE115", _ => "\uE8A5"
    };

    private static Style NavigationItemStyle()
    {
        var style = Ui.ExtendStyle<TreeViewItem>();
        style.Setters.Add(new Setter(Control.ForegroundProperty, Ui.Ink));
        style.Setters.Add(new Setter(Control.BackgroundProperty, Ui.Paper));
        style.Setters.Add(new Setter(Control.BorderBrushProperty, Ui.Paper));
        style.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(3, 0, 0, 0)));
        var panel = new FrameworkElementFactory(typeof(StackPanel));
        var frame = new FrameworkElementFactory(typeof(Border), "NavigationRow");
        frame.SetValue(Border.CornerRadiusProperty, new CornerRadius(5));
        frame.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 2, 0, 2));
        frame.SetBinding(Border.BackgroundProperty, new Binding(nameof(Control.Background)) { RelativeSource = RelativeSource.TemplatedParent });
        frame.SetBinding(Border.BorderBrushProperty, new Binding(nameof(Control.BorderBrush)) { RelativeSource = RelativeSource.TemplatedParent });
        frame.SetBinding(Border.BorderThicknessProperty, new Binding(nameof(Control.BorderThickness)) { RelativeSource = RelativeSource.TemplatedParent });
        var row = new FrameworkElementFactory(typeof(DockPanel));
        var toggle = new FrameworkElementFactory(typeof(ToggleButton), "BranchToggle");
        toggle.SetValue(FrameworkElement.WidthProperty, 20d);
        toggle.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        toggle.SetValue(UIElement.FocusableProperty, false);
        toggle.SetBinding(ToggleButton.IsCheckedProperty, new Binding(nameof(TreeViewItem.IsExpanded)) { RelativeSource = RelativeSource.TemplatedParent, Mode = BindingMode.TwoWay });
        var arrow = new FrameworkElementFactory(typeof(TextBlock));
        arrow.SetValue(TextBlock.TextProperty, "›"); arrow.SetValue(TextBlock.FontSizeProperty, 18d);
        arrow.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        var toggleTemplate = new ControlTemplate(typeof(ToggleButton)) { VisualTree = arrow };
        toggleTemplate.Triggers.Add(new Trigger { Property = ToggleButton.IsCheckedProperty, Value = true,
            Setters = { new Setter(UIElement.RenderTransformProperty, new RotateTransform(90)), new Setter(UIElement.RenderTransformOriginProperty, new Point(.5, .5)) } });
        toggle.SetValue(Control.TemplateProperty, toggleTemplate);
        row.AppendChild(toggle);
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter), "PART_Header");
        presenter.SetValue(ContentPresenter.ContentSourceProperty, "Header");
        presenter.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 10, 8, 10));
        row.AppendChild(presenter); frame.AppendChild(row); panel.AppendChild(frame);
        var children = new FrameworkElementFactory(typeof(ItemsPresenter), "Children");
        children.SetValue(FrameworkElement.MarginProperty, new Thickness(14, 0, 0, 0)); panel.AppendChild(children);
        var template = new ControlTemplate(typeof(TreeViewItem)) { VisualTree = panel };
        template.Triggers.Add(new Trigger { Property = ItemsControl.HasItemsProperty, Value = false,
            Setters = { new Setter(UIElement.VisibilityProperty, Visibility.Hidden, "BranchToggle") } });
        template.Triggers.Add(new Trigger { Property = TreeViewItem.IsExpandedProperty, Value = false,
            Setters = { new Setter(UIElement.VisibilityProperty, Visibility.Collapsed, "Children") } });
        style.Setters.Add(new Setter(Control.TemplateProperty, template));
        template.Triggers.Add(new Trigger { SourceName = "NavigationRow", Property = UIElement.IsMouseOverProperty, Value = true,
            Setters = { new Setter(Border.BackgroundProperty, Ui.Hover, "NavigationRow"),
                new Setter(System.Windows.Documents.TextElement.ForegroundProperty, Ui.Ink, "NavigationRow") } });
        template.Triggers.Add(new Trigger { Property = TreeViewItem.IsSelectedProperty, Value = true,
            Setters = { new Setter(Border.BackgroundProperty, Ui.Selection, "NavigationRow"),
                new Setter(System.Windows.Documents.TextElement.ForegroundProperty, Ui.SelectionText, "NavigationRow") } });
        style.Triggers.Add(new Trigger { Property = TreeViewItem.IsSelectedProperty, Value = true,
            Setters = { new Setter(Control.BackgroundProperty, Ui.Selection), new Setter(Control.ForegroundProperty, Ui.SelectionText),
                new Setter(Control.BorderBrushProperty, Ui.Accent), new Setter(Control.FontWeightProperty, FontWeights.SemiBold) } });
        return style;
    }

    private static DataTemplate ItemTemplate()
    {
        var row = new FrameworkElementFactory(typeof(DockPanel));
        var icon = new FrameworkElementFactory(typeof(TextBlock), "NavigationIcon");
        icon.SetValue(TextBlock.FontFamilyProperty, new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"));
        icon.SetValue(TextBlock.FontSizeProperty, 15d); icon.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 1, 8, 0));
        icon.SetBinding(TextBlock.TextProperty, new Binding(nameof(NavigationItem.Icon))); row.AppendChild(icon);
        var label = new FrameworkElementFactory(typeof(TextBlock));
        label.SetBinding(TextBlock.TextProperty, new Binding(nameof(NavigationItem.Label)));
        label.SetValue(TextBlock.TextWrappingProperty, TextWrapping.Wrap); row.AppendChild(label);
        var template = new DataTemplate(typeof(NavigationItem)) { VisualTree = row };
        template.Triggers.Add(new DataTrigger { Binding = new Binding(nameof(NavigationItem.Icon)), Value = "",
            Setters = { new Setter(UIElement.VisibilityProperty, Visibility.Collapsed, "NavigationIcon") } });
        return template;
    }

    private static ControlTemplate ContentOnlyTemplate()
    {
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter), "PART_SelectedContentHost");
        presenter.SetValue(ContentPresenter.ContentSourceProperty, "SelectedContent");
        presenter.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Stretch);
        presenter.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Stretch);
        // Standard tab peers require realized TabItem containers as well as the content host.
        // Hidden keeps the item host in layout; Collapsed would remove the selected page from UIA.
        var root = new FrameworkElementFactory(typeof(Grid));
        var headers = new FrameworkElementFactory(typeof(ItemsPresenter));
        headers.SetValue(FrameworkElement.WidthProperty, 0d);
        headers.SetValue(FrameworkElement.HeightProperty, 0d);
        headers.SetValue(UIElement.VisibilityProperty, Visibility.Hidden);
        headers.SetValue(UIElement.IsHitTestVisibleProperty, false);
        root.AppendChild(headers);
        root.AppendChild(presenter);
        return new ControlTemplate(typeof(TabControl)) { VisualTree = root };
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        navigation.SelectedItemChanged -= NavigationSelected; search.TextChanged -= SearchChanged;
        foreach (var (tab, collection) in observed)
        {
            tab.SelectionChanged -= MountSelected; collection.CollectionChanged -= MountItemsChanged;
        }
        observed.Clear(); pageNodes.Clear(); categoryNodes.Clear(); PaneSizeChanged = null;
    }

    internal sealed record NavigationItem(TabItem Tab, TabItem? Category, string Label, string Icon);
}

