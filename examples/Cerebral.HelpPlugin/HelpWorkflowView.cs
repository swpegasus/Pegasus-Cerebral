using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using Cerebral.Desktop.Plugins;

namespace Cerebral.HelpPlugin;

/// <summary>Local diagram selection and navigation only; no host, provider or store access.</summary>
public sealed class HelpWorkflowView : UserControl
{
    private readonly Grid nodes = new();
    private readonly Canvas connections = new() { IsHitTestVisible = false };
    private readonly StackPanel details = new();
    private readonly Dictionary<string, Button> buttons = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<TextBlock>> labels = new(StringComparer.Ordinal);
    private readonly List<(string From, string To, Shape Mark)> marks = [];
    private readonly Action<string, string> openReference;
    private readonly HelpFlowStage[] primary = HelpWorkflowCatalog.Stages.Where(s => !s.Optional).ToArray();
    private readonly Grid branches = new();
    private int columns;
    private string selected;
    private string? preview;

    public HelpWorkflowView(Action<string, string> openReference, string initial = "request")
    {
        this.openReference = openReference;
        selected = HelpWorkflowCatalog.Stages.Any(s => s.Id == initial) ? initial : "request";
        AutomationProperties.SetAutomationId(this, "HelpWorkflow");
        AutomationProperties.SetName(this, "Conceptual workflow, source map and intended boundaries");
        var body = new StackPanel();
        body.Children.Add(Text("Workflow", 18, true));
        body.Children.Add(Text(HelpWorkflowCatalog.EvidenceLabel, muted: true));
        body.Children.Add(Text("Arrows show the model path. Context and engine decisions repeat; durable state begins at admission. Select a stage for details.", muted: true));
        var graph = new Grid { Margin = new Thickness(0, 10, 0, 4) };
        graph.Children.Add(connections);
        graph.Children.Add(nodes);
        body.Children.Add(graph);
        var loops = new WrapPanel { Margin = new Thickness(0, 4, 0, 8) };
        foreach (var loop in new[] { ("engine", "Result → next engine decision"), ("persistence", "Admission ↔ durable state") })
        {
            var button = new Button { Content = Text(loop.Item2, 12), Padding = new Thickness(8, 4, 8, 4), Margin = new Thickness(0, 0, 8, 4) };
            AutomationProperties.SetAutomationId(button, "HelpFlowLoop." + loop.Item1);
            button.Click += (_, _) => Select(loop.Item1);
            loops.Children.Add(button);
        }
        body.Children.Add(loops);
        foreach (var stage in primary) nodes.Children.Add(CreateButton(stage));
        body.Children.Add(Text("Optional contributions and setup", 15, true));
        branches.Margin = new Thickness(0, 6, 0, 8);
        foreach (var stage in HelpWorkflowCatalog.Stages.Where(s => s.Optional))
            branches.Children.Add(CreateButton(stage));
        body.Children.Add(branches);
        details.Margin = new Thickness(0, 8, 0, 8);
        AutomationProperties.SetAutomationId(details, "HelpFlowDetails");
        body.Children.Add(details);
        Content = body;
        SizeChanged += (_, _) => Reflow(ActualWidth);
        nodes.SizeChanged += (_, _) => DrawConnections();
        Reflow(0);
        Select(selected);
    }

    public event Action<string>? StageSelected;
    public string SelectedStageId => selected;

    private Button CreateButton(HelpFlowStage stage)
    {
        var title = Text(stage.Title, 14, true);
        var caption = Text(stage.Caption, 12);
        var stack = new StackPanel();
        stack.Children.Add(title); stack.Children.Add(caption);
        labels[stage.Id] = [title, caption];
        var button = new Button
        {
            Content = stack, Tag = stage.Id, Margin = new Thickness(0),
            Padding = new Thickness(10, 6, 10, 6), MinHeight = 64,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            ToolTip = stage.Title + ": " + stage.Input + " → " + stage.Output
        };
        AutomationProperties.SetAutomationId(button, "HelpFlow." + stage.Id);
        AutomationProperties.SetName(button, "Select " + stage.Title + ". " + stage.Caption);
        button.Click += (_, _) => Select(stage.Id);
        button.MouseEnter += (_, _) => HighlightPreview(stage.Id);
        button.MouseLeave += (_, _) => { preview = null; ApplyHighlight(); };
        button.GotKeyboardFocus += (_, _) => HighlightPreview(stage.Id);
        button.LostKeyboardFocus += (_, _) => { preview = null; ApplyHighlight(); };
        buttons[stage.Id] = button;
        return button;
    }

    private void HighlightPreview(string id) { preview = id; ApplyHighlight(); }

    private void Select(string id)
    {
        selected = id;
        preview = null;
        var stage = HelpWorkflowCatalog.Stages.Single(s => s.Id == id);
        details.Children.Clear();
        var title = Text("Selected: " + stage.Title, 16, true);
        AutomationProperties.SetAutomationId(title, "HelpFlowDetailTitle");
        details.Children.Add(title);
        details.Children.Add(Text("Owner · " + stage.Owner, muted: true));
        details.Children.Add(Text("In · " + stage.Input));
        details.Children.Add(Text("Out · " + stage.Output));
        var current = Text("Current source · " + stage.Current);
        AutomationProperties.SetAutomationId(current, "HelpFlowCurrent");
        details.Children.Add(current);
        var target = Text("Intended design · " + stage.Target, muted: true);
        AutomationProperties.SetAutomationId(target, "HelpFlowTarget");
        details.Children.Add(target);
        var link = new Button
        {
            Content = "Help: " + HelpCatalog.GetPage(stage.PageId).Title + " / " +
                HelpCatalog.GetPage(stage.PageId).Sections.Single(s => s.Id == stage.SectionId).Title,
            Padding = new Thickness(10, 5, 10, 5), Margin = new Thickness(0, 6, 0, 6),
            HorizontalAlignment = HorizontalAlignment.Left, MaxWidth = 550
        };
        link.Content = Text((string)link.Content, 13);
        AutomationProperties.SetAutomationId(link, "HelpFlowReference");
        AutomationProperties.SetName(link, "Open Help section " + stage.PageId + " " + stage.SectionId);
        link.Click += (_, _) => openReference(stage.PageId, stage.SectionId);
        details.Children.Add(link);
        var contract = ContractDetails(stage);
        var developer = new Expander { Header = "Developer contract points", Content = contract, Margin = new Thickness(0, 4, 0, 0) };
        developer.SetResourceReference(ForegroundProperty, DesktopThemeResourceKeys.TextBrush);
        AutomationProperties.SetAutomationId(developer, "HelpFlowDeveloper");
        details.Children.Add(developer);
        ApplyHighlight();
        StageSelected?.Invoke(id);
    }

    private void Reflow(double width)
    {
        var next = width >= 620 ? 3 : width >= 380 ? 2 : 1;
        if (columns == next) { DrawConnections(); return; }
        columns = next;
        nodes.ColumnDefinitions.Clear(); nodes.RowDefinitions.Clear();
        for (var column = 0; column < columns * 2 - 1; column++)
            nodes.ColumnDefinitions.Add(new() { Width = column % 2 == 0 ? new GridLength(1, GridUnitType.Star) : new GridLength(26) });
        var rows = (primary.Length + columns - 1) / columns;
        for (var row = 0; row < rows * 2 - 1; row++)
            nodes.RowDefinitions.Add(new() { Height = row % 2 == 0 ? GridLength.Auto : new GridLength(24) });
        for (var index = 0; index < primary.Length; index++)
        {
            var row = index / columns;
            var column = row % 2 == 0 ? index % columns : columns - 1 - index % columns;
            Grid.SetRow(buttons[primary[index].Id], row * 2);
            Grid.SetColumn(buttons[primary[index].Id], column * 2);
        }
        branches.ColumnDefinitions.Clear(); branches.RowDefinitions.Clear();
        var branchColumns = width >= 620 ? 4 : width >= 380 ? 2 : 1;
        for (var col = 0; col < branchColumns; col++) branches.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        var optional = HelpWorkflowCatalog.Stages.Where(s => s.Optional).ToArray();
        for (var row = 0; row < (optional.Length + branchColumns - 1) / branchColumns; row++)
            branches.RowDefinitions.Add(new() { Height = GridLength.Auto });
        for (var i = 0; i < optional.Length; i++)
        {
            var button = buttons[optional[i].Id];
            button.Margin = new Thickness(0, 0, i % branchColumns == branchColumns - 1 ? 0 : 8, 8);
            Grid.SetColumn(button, i % branchColumns); Grid.SetRow(button, i / branchColumns);
        }
        // Layout owns positions; redraw after the new grid has arranged, without a retained timer.
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, new Action(DrawConnections));
    }

    private void DrawConnections()
    {
        connections.Children.Clear(); marks.Clear();
        if (nodes.ActualWidth <= 0) return;
        for (var index = 0; index < primary.Length - 1; index++)
        {
            var from = buttons[primary[index].Id]; var to = buttons[primary[index + 1].Id];
            var a = from.TranslatePoint(new Point(), nodes); var b = to.TranslatePoint(new Point(), nodes);
            Point start; Point end;
            if (Grid.GetRow(from) == Grid.GetRow(to))
            {
                var right = b.X > a.X;
                start = new Point(right ? a.X + from.ActualWidth : a.X, a.Y + from.ActualHeight / 2);
                end = new Point(right ? b.X : b.X + to.ActualWidth, b.Y + to.ActualHeight / 2);
            }
            else
            {
                start = new Point(a.X + from.ActualWidth / 2, a.Y + from.ActualHeight);
                end = new Point(b.X + to.ActualWidth / 2, b.Y);
            }
            var line = new Line { X1 = start.X, Y1 = start.Y, X2 = end.X, Y2 = end.Y, StrokeThickness = 1.5 };
            AddMark(primary[index].Id, primary[index + 1].Id, line);
            var direction = end - start;
            if (direction.Length < 1) continue;
            direction.Normalize();
            var normal = new Vector(-direction.Y, direction.X);
            var arrow = new Polygon { Points = new PointCollection { end, end - direction * 7 + normal * 3.5, end - direction * 7 - normal * 3.5 } };
            AddMark(primary[index].Id, primary[index + 1].Id, arrow);
        }
        ApplyHighlight();
    }

    private void AddMark(string from, string to, Shape mark)
    {
        AutomationProperties.SetAutomationId(mark, "HelpFlowEdge." + from + "." + to);
        marks.Add((from, to, mark)); connections.Children.Add(mark);
    }

    private void ApplyHighlight()
    {
        var active = preview ?? selected;
        var stage = HelpWorkflowCatalog.Stages.Single(s => s.Id == active);
        var related = stage.ConnectsTo ?? [];
        foreach (var pair in buttons)
        {
            var isSelected = pair.Key == selected;
            var isActive = pair.Key == active || related.Contains(pair.Key);
            pair.Value.SetResourceReference(Control.BackgroundProperty,
                isSelected ? SystemColors.HighlightBrushKey : isActive ? SystemColors.InactiveSelectionHighlightBrushKey : SystemColors.ControlBrushKey);
            pair.Value.SetResourceReference(Control.BorderBrushProperty,
                isActive ? SystemColors.HighlightBrushKey : SystemColors.ControlDarkBrushKey);
            foreach (var label in labels[pair.Key])
                label.SetResourceReference(TextBlock.ForegroundProperty,
                    isSelected ? SystemColors.HighlightTextBrushKey : DesktopThemeResourceKeys.TextBrush);
            AutomationProperties.SetItemStatus(pair.Value, isSelected ? "Selected" : isActive ? "Related / highlighted" : "");
        }
        foreach (var (from, to, mark) in marks)
        {
            var highlighted = from == active || to == active || related.Contains(from) || related.Contains(to);
            var brush = highlighted ? SystemColors.HighlightBrushKey : SystemColors.ControlDarkBrushKey;
            mark.SetResourceReference(Shape.StrokeProperty, brush);
            mark.SetResourceReference(Shape.FillProperty, brush);
            AutomationProperties.SetItemStatus(mark, highlighted ? "Highlighted" : "");
        }
    }

    internal static StackPanel ContractDetails(HelpFlowStage stage)
    {
        var contract = new StackPanel();
        contract.Children.Add(Text("Identity / version · " + stage.Contract));
        contract.Children.Add(Text("Entry · " + stage.Entry));
        contract.Children.Add(Text("Exit · " + stage.Exit));
        contract.Children.Add(Text("Failure / cancellation · " + stage.FailureCancellation));
        contract.Children.Add(Text("Source / guidance · " + stage.Source, 12, muted: true));
        return contract;
    }

    private static TextBlock Text(string value, double size = 14, bool heading = false, bool muted = false)
    {
        var text = new TextBlock
        {
            Text = value, FontSize = size, FontWeight = heading ? FontWeights.SemiBold : FontWeights.Normal,
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 2)
        };
        text.SetResourceReference(TextBlock.ForegroundProperty, muted ? DesktopThemeResourceKeys.MutedTextBrush : DesktopThemeResourceKeys.TextBrush);
        return text;
    }
}
