using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using Cerebral.Desktop.Plugins;

namespace Cerebral.HelpPlugin;

/// <summary>Native navigation over passive content. Every interaction changes this view only.</summary>
public sealed class HelpView : UserControl
{
    private readonly ListBox navigation = new();
    private readonly StackPanel pageBody = new();
    private readonly ComboBox compactNavigation = new();
    private readonly ColumnDefinition navigationColumn = new();
    private string selectedStage = "n:core";
    private string selectedScope = "overview";
    private Border? linkedSection;
    private Expander? linkedPoint;
    private Border? referenceError;
    private FrameworkElement? referenceTarget;
    private readonly Dictionary<string, ListBoxItem> navigationItems = new(StringComparer.Ordinal);

    public HelpView()
    {
        AutomationProperties.SetAutomationId(this, "CerebralHelp");
        AutomationProperties.SetName(this, "Cerebral Help");
        var layout = new Grid();
        layout.RowDefinitions.Add(new() { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });

        var header = new StackPanel { Margin = new Thickness(16, 12, 16, 12) };
        header.Children.Add(Text("Help", 24, true));
        header.Children.Add(Text("Tutorials/images pending · assistant connection pending", muted: true));
        compactNavigation.ItemsSource = HelpCatalog.Pages;
        compactNavigation.DisplayMemberPath = nameof(HelpPage.Title);
        compactNavigation.Margin = new Thickness(0, 10, 0, 0);
        AutomationProperties.SetAutomationId(compactNavigation, "HelpCompactNavigation");
        AutomationProperties.SetName(compactNavigation, "Help page");
        header.Children.Add(compactNavigation);
        layout.Children.Add(header);

        var main = new Grid();
        main.ColumnDefinitions.Add(navigationColumn);
        main.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetRow(main, 1);
        layout.Children.Add(main);

        navigation.Margin = new Thickness(8, 0, 8, 12);
        navigation.BorderThickness = new Thickness(0);
        navigation.HorizontalContentAlignment = HorizontalAlignment.Stretch;
        navigation.SetResourceReference(ForegroundProperty, DesktopThemeResourceKeys.TextBrush);
        navigation.SetResourceReference(BackgroundProperty, SystemColors.WindowBrushKey);
        AutomationProperties.SetAutomationId(navigation, "HelpNavigation");
        AutomationProperties.SetName(navigation, "Help pages");
        foreach (var page in HelpCatalog.Pages)
        {
            var item = new ListBoxItem { Content = page.Title, Tag = page.Id, Padding = new Thickness(8, 5, 8, 5) };
            AutomationProperties.SetAutomationId(item, "HelpNav." + page.Id);
            AutomationProperties.SetName(item, page.Title);
            navigationItems.Add(page.Id, item);
            navigation.Items.Add(item);
        }
        navigation.SelectionChanged += (_, _) =>
        {
            if (navigation.SelectedItem is ListBoxItem { Tag: string id })
            {
                var selected = HelpCatalog.Pages.FirstOrDefault(page => page.Id == id);
                if (selected is null) { ShowReferenceError(); return; }
                compactNavigation.SelectedItem = selected;
                ShowPage(selected);
            }
        };
        compactNavigation.SelectionChanged += (_, _) =>
        {
            if (compactNavigation.SelectedItem is HelpPage page)
            {
                if (navigationItems.TryGetValue(page.Id, out var item)) navigation.SelectedItem = item;
                else ShowReferenceError();
            }
        };
        main.Children.Add(navigation);

        var scroll = new ScrollViewer
        {
            Content = pageBody, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Padding = new Thickness(16, 0, 16, 16)
        };
        Grid.SetColumn(scroll, 1);
        main.Children.Add(scroll);
        Content = layout;
        SetResourceReference(BackgroundProperty, SystemColors.WindowBrushKey);
        SizeChanged += (_, _) => UpdateNavigationLayout(ActualWidth);
        UpdateNavigationLayout(0);
        if (navigationItems.TryGetValue("overview", out var overview)) navigation.SelectedItem = overview;
        else ShowReferenceError();
    }

    private void UpdateNavigationLayout(double width)
    {
        var compact = width < 640;
        navigation.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        navigationColumn.Width = new GridLength(compact ? 0 : 180);
        compactNavigation.Visibility = compact ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ShowPage(HelpPage page)
    {
        if (pageBody.Children.OfType<HelpArchitectureView>().FirstOrDefault() is { } architecture)
        {
            selectedStage = architecture.SelectedReference;
            selectedScope = architecture.ScopeId;
        }
        ClearReferenceState();
        pageBody.Children.Clear();
        pageBody.Tag = page.Id;
        AutomationProperties.SetAutomationId(pageBody, "HelpPage." + page.Id);
        var title = Text(page.Title, 22, true);
        AutomationProperties.SetAutomationId(title, "HelpPageTitle");
        pageBody.Children.Add(title);
        pageBody.Children.Add(Text(page.Summary, muted: true));
        if (page.Id == "overview") { pageBody.Children.Add(CommonIssuesLink()); AddDiagram(); }
        if (page.Id == "assistant") AddAssistant();
        foreach (var section in page.Sections)
        {
            var body = new StackPanel();
            body.Children.Add(Text(section.Title, 16, true));
            body.Children.Add(Text(section.Text, muted: true));
            if (page.Id == "common-issues" && section.Id == "catalog") body.Children.Add(new HelpRecoveryView());
            if (page.Id == "reference" && section.Id == "recovery") body.Children.Add(CommonIssuesLink());
            if (page.Id == "reference" && section.Id == "discovery")
            {
                foreach (var point in HelpDiscoveryCatalog.Points)
                {
                    var content = new StackPanel();
                    content.Children.Add(Text(point.Identity));
                    content.Children.Add(Text(point.InputOutput));
                    content.Children.Add(Text(point.Boundary, muted: true));
                    content.Children.Add(Text(point.Source, 12, muted: true));
                    var link = new Button { Content = "Help: " + point.HelpPageId + " / " + point.HelpSectionId, HorizontalAlignment = HorizontalAlignment.Left, Padding = new Thickness(8, 4, 8, 4) };
                    AutomationProperties.SetAutomationId(link, "HelpDiscoveryLink." + point.Id);
                    AutomationProperties.SetName(link, "Open Help " + point.HelpPageId + " " + point.HelpSectionId);
                    link.Click += (_, _) => OpenReferencePoint(point.HelpPageId, point.HelpSectionId,
                        point.HelpPageId == "reference" && point.HelpSectionId == "discovery" ? point.ReferenceId : null);
                    content.Children.Add(link);
                    var entry = new Expander { Header = point.Title, Content = content };
                    entry.SetResourceReference(ForegroundProperty, DesktopThemeResourceKeys.TextBrush);
                    AutomationProperties.SetAutomationId(entry, "HelpDiscovery." + point.Id);
                    body.Children.Add(entry);
                }
            }
            var nodes = HelpArchitectureCatalog.Nodes.Where(node => node.PageId == page.Id && node.SectionId == section.Id).ToArray();
            var edges = HelpArchitectureCatalog.Edges.Where(edge => edge.PageId == page.Id && edge.SectionId == section.Id).ToArray();
            foreach (var node in nodes)
            {
                var content = new StackPanel();
                content.Children.Add(Text("Owner · " + node.Owner));
                content.Children.Add(Text("Current source · " + node.Current));
                content.Children.Add(Text("Intended boundary · " + node.Target));
                content.Children.Add(Text("Source · " + node.Source, 12, muted: true));
                body.Children.Add(ReferencePoint("n:" + node.Id, node.Title, content));
            }
            foreach (var edge in edges)
            {
                var content = new StackPanel();
                content.Children.Add(Text("Direction / ports · " + edge.From + " [" + edge.FromPort + "] → " + edge.To + " [" + edge.ToPort + "]"));
                content.Children.Add(Text("Payload · " + edge.Payload));
                content.Children.Add(Text("Contract / identity · " + edge.Contract));
                content.Children.Add(Text("Status · " + edge.Status));
                content.Children.Add(Text("Failure / cancellation · " + edge.FailureCancellation));
                content.Children.Add(Text("Source · " + edge.Source, 12, muted: true));
                body.Children.Add(ReferencePoint("e:" + edge.Id, edge.From + " → " + edge.To + " · " + edge.Label, content));
            }
            if (section.Kind == HelpSectionKind.Screenshot)
            {
                body.Children.Add(Text("IMAGE PLACEHOLDER", muted: true));
                body.MinHeight = 120;
            }
            var card = Card(body);
            AutomationProperties.SetAutomationId(card, "HelpSection." + page.Id + "." + section.Id);
            AutomationProperties.SetName(card, section.Title + (section.Kind == HelpSectionKind.Screenshot ? ", image placeholder" : section.Kind == HelpSectionKind.Reference || nodes.Length + edges.Length > 0 || (page.Id == "reference" && section.Id == "discovery") ? ", source contract reference" : ", content pending"));
            pageBody.Children.Add(card);
        }
    }
    private static Expander ReferencePoint(string id, string heading, StackPanel content)
    {
        var entry = new Expander { Header = Text(heading, 14, true), Content = content };
        entry.SetResourceReference(ForegroundProperty, DesktopThemeResourceKeys.TextBrush);
        AutomationProperties.SetAutomationId(entry, "HelpArchitecturePoint." + id);
        AutomationProperties.SetName(entry, heading + ", contract reference");
        return entry;
    }

    private void AddDiagram()
    {
        var workflow = new HelpArchitectureView(OpenReference, selectedStage, selectedScope);
        workflow.SelectionChanged += id => selectedStage = id;
        pageBody.Children.Add(workflow);
    }

    // Keep the two-argument map callback; discovery links carry their own reference identity.
    private void OpenReference(string pageId, string sectionId) => OpenReferencePoint(pageId, sectionId, selectedStage);

    private void OpenReferencePoint(string pageId, string sectionId, string? referenceId)
    {
        ClearReferenceState();
        var page = HelpCatalog.Pages.FirstOrDefault(candidate => candidate.Id == pageId);
        if (page is null || !page.Sections.Any(candidate => candidate.Id == sectionId) ||
            !navigationItems.TryGetValue(pageId, out var item))
        { ShowReferenceError(); return; }
        navigation.SelectedItem = item;
        if (!Equals(pageBody.Tag, pageId)) ShowPage(page);
        var section = pageBody.Children.OfType<Border>().FirstOrDefault(card =>
            AutomationProperties.GetAutomationId(card) == "HelpSection." + pageId + "." + sectionId);
        if (section?.Child is not StackPanel body) { ShowReferenceError(); return; }
        linkedSection = section;
        section.SetResourceReference(Border.BorderBrushProperty, SystemColors.HighlightBrushKey);
        section.BorderThickness = new Thickness(2);
        AutomationProperties.SetItemStatus(section, "Linked section");
        body.Tag = "Linked section";
        var pointId = referenceId is null ? null : referenceId.StartsWith("d:", StringComparison.Ordinal)
            ? "HelpDiscovery." + referenceId[2..] : "HelpArchitecturePoint." + referenceId;
        var point = pointId is null ? null : body.Children.OfType<Expander>().FirstOrDefault(entry =>
            AutomationProperties.GetAutomationId(entry) == pointId);
        if (pointId is not null && point is null) { ShowReferenceError(); return; }
        if (point is not null)
        {
            linkedPoint = point;
            point.IsExpanded = true;
            AutomationProperties.SetItemStatus(point, "Linked contract point");
        }
        referenceTarget = point as FrameworkElement ?? section;
        referenceTarget.Focusable = true;
        ScheduleReferenceFocus(referenceTarget);
    }

    private Button CommonIssuesLink()
    {
        var link = new Button { Content = "Common issues and recovery", HorizontalAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(8, 5, 8, 5), Margin = new Thickness(0, 4, 0, 8) };
        AutomationProperties.SetAutomationId(link, "HelpCommonIssues");
        AutomationProperties.SetName(link, "Open common issues and recovery");
        link.Click += (_, _) => OpenReferencePoint("common-issues", "catalog", null);
        return link;
    }

    private void ClearReferenceState()
    {
        referenceTarget = null;
        if (linkedPoint is not null) AutomationProperties.SetItemStatus(linkedPoint, "");
        linkedPoint = null;
        if (linkedSection is not null)
        {
            linkedSection.SetResourceReference(Border.BorderBrushProperty, SystemColors.ControlDarkBrushKey);
            linkedSection.BorderThickness = new Thickness(1);
            AutomationProperties.SetItemStatus(linkedSection, "");
            if (linkedSection.Child is FrameworkElement body && Equals(body.Tag, "Linked section"))
                body.ClearValue(FrameworkElement.TagProperty);
        }
        linkedSection = null;
        if (referenceError is not null) pageBody.Children.Remove(referenceError);
        referenceError = null;
    }

    private void ShowReferenceError()
    {
        ClearReferenceState();
        referenceError = Card(Text("This Help reference is no longer available. Choose a page from Help navigation."));
        AutomationProperties.SetAutomationId(referenceError, "HelpReferenceUnavailable");
        AutomationProperties.SetName(referenceError, "Help reference unavailable");
        referenceError.Focusable = true;
        pageBody.Children.Insert(Math.Min(2, pageBody.Children.Count), referenceError);
        referenceTarget = referenceError;
        ScheduleReferenceFocus(referenceError);
    }

    private void ScheduleReferenceFocus(FrameworkElement target)
    {
        var pageIdentity = pageBody.Tag;
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, new Action(() =>
        {
            // Another navigation may replace the destination before the loaded-priority callback.
            if (!IsLoaded || !ReferenceEquals(referenceTarget, target) ||
                !Equals(pageBody.Tag, pageIdentity) || !pageBody.IsAncestorOf(target)) return;
            target.BringIntoView();
            target.Focus();
        }));
    }

    private void AddAssistant()
    {
        pageBody.Children.Add(Card(Text("Connection pending. This panel has no active assistant, messages or provider.", muted: true)));
        var transcript = Card(Text("Empty conversation placeholder", muted: true));
        transcript.MinHeight = 140;
        AutomationProperties.SetAutomationId(transcript, "HelpAssistantConversation");
        AutomationProperties.SetName(transcript, "Help assistant conversation, empty and not connected");
        pageBody.Children.Add(transcript);
        var label = Text("Message", 14, true);
        var input = new TextBox
        {
            Text = "", IsEnabled = false, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap,
            MinHeight = 76, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Margin = new Thickness(0, 6, 0, 6), ToolTip = "Available after a reviewed Help assistant connection is implemented."
        };
        AutomationProperties.SetAutomationId(input, "HelpAssistantInput");
        AutomationProperties.SetName(input, "Help assistant message, disabled until connection");
        pageBody.Children.Add(label);
        pageBody.Children.Add(input);
        var send = new Button
        {
            Content = "Send", IsEnabled = false, HorizontalAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(18, 6, 18, 6), ToolTip = "Assistant connection pending"
        };
        AutomationProperties.SetAutomationId(send, "HelpAssistantSend");
        AutomationProperties.SetName(send, "Send Help message, unavailable");
        pageBody.Children.Add(send);
    }

    private static Border Card(UIElement child)
    {
        var card = new Border
        {
            Child = child, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4),
            Padding = new Thickness(12), Margin = new Thickness(0, 10, 0, 0)
        };
        card.SetResourceReference(Border.BorderBrushProperty, SystemColors.ControlDarkBrushKey);
        return card;
    }

    private static TextBlock Text(string value, double fontSize = 14, bool heading = false, bool muted = false)
    {
        var text = new TextBlock
        {
            Text = value, TextWrapping = TextWrapping.Wrap, FontSize = fontSize,
            FontWeight = heading ? FontWeights.SemiBold : FontWeights.Normal,
            Margin = new Thickness(0, 3, 0, 3)
        };
        text.SetResourceReference(TextBlock.ForegroundProperty,
            muted ? DesktopThemeResourceKeys.MutedTextBrush : DesktopThemeResourceKeys.TextBrush);
        return text;
    }
}
