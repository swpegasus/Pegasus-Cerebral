using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Cerebral.Desktop.Plugins;

namespace Cerebral.HelpPlugin;

/// <summary>One authored issue at a time. The selector never executes the documented operations.</summary>
public sealed class HelpRecoveryView : StackPanel
{
    private readonly ComboBox selector = new();
    private readonly StackPanel detail = new();
    public string SelectedIssueId => (selector.SelectedItem as HelpRecoverySummary)?.Id ?? "";

    public HelpRecoveryView()
    {
        AutomationProperties.SetAutomationId(this, "HelpRecovery");
        Children.Add(Text("Choose the notice or blocker you encountered. These are source-backed instructions, not a live diagnosis."));
        selector.ItemsSource = HelpRecoveryCatalog.List();
        selector.DisplayMemberPath = nameof(HelpRecoverySummary.Title);
        selector.HorizontalAlignment = HorizontalAlignment.Stretch;
        selector.Margin = new Thickness(0, 8, 0, 12);
        AutomationProperties.SetAutomationId(selector, "HelpRecoverySelector");
        AutomationProperties.SetName(selector, "Common issue");
        selector.SelectionChanged += (_, _) =>
        {
            if (selector.SelectedItem is HelpRecoverySummary summary) Show(HelpRecoveryCatalog.Get(summary.Id));
        };
        Children.Add(selector);
        Children.Add(detail);
        selector.SelectedIndex = 0;
    }

    private void Show(HelpRecoveryEntry entry)
    {
        detail.Children.Clear();
        AutomationProperties.SetAutomationId(detail, "HelpRecoveryIssue." + entry.Id);
        AutomationProperties.SetName(detail, entry.Title);
        detail.Children.Add(Text(entry.Title, true));
        detail.Children.Add(Text("Help ID: " + entry.Id));
        detail.Children.Add(Text("Severity: " + entry.Severity + " · authored guidance; current state must be checked"));
        detail.Children.Add(Text("Why this appears: " + entry.Reason));
        detail.Children.Add(Text("When it blocks: " + entry.BlockingCondition));
        foreach (var action in entry.Actions)
        {
            var body = new StackPanel { Margin = new Thickness(0, 8, 0, 8) };
            var availability = action.Availability switch
            {
                HelpRecoveryAvailability.SupportedReadOnly => "Supported read-only check",
                HelpRecoveryAvailability.OwnerAction => "Supported owner/maintainer action · use normal admission",
                HelpRecoveryAvailability.ScopedAction => "Supported action · requires the admitted run scope",
                _ => "Pending support · action unavailable"
            };
            body.Children.Add(Text(availability, true));
            body.Children.Add(Text(action.Steps));
            body.Children.Add(Text("Check afterward: " + action.Readback));
            var technical = new Expander { Header = "Existing route and authority", Content = Text(action.Route) };
            technical.SetResourceReference(Control.ForegroundProperty, DesktopThemeResourceKeys.TextBrush);
            body.Children.Add(technical);
            var card = new GroupBox { Header = action.Title, Content = body, Margin = new Thickness(0, 8, 0, 0) };
            card.SetResourceReference(Control.ForegroundProperty, DesktopThemeResourceKeys.TextBrush);
            AutomationProperties.SetAutomationId(card, "HelpRecoveryAction." + entry.Id + "." + action.Id);
            AutomationProperties.SetItemStatus(card, action.Availability.ToString());
            detail.Children.Add(card);
        }
        var sources = new StackPanel();
        foreach (var source in entry.Sources) sources.Children.Add(Text(source.Path + " · " + source.Symbol));
        var references = new Expander { Header = "Source and contract references", Content = sources, Margin = new Thickness(0, 12, 0, 0) };
        references.SetResourceReference(Control.ForegroundProperty, DesktopThemeResourceKeys.TextBrush);
        detail.Children.Add(references);
    }

    private static TextBlock Text(string value, bool heading = false)
    {
        var block = new TextBlock { Text = value, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 5),
            FontSize = heading ? 16 : 13, FontWeight = heading ? FontWeights.SemiBold : FontWeights.Normal };
        block.SetResourceReference(TextBlock.ForegroundProperty, DesktopThemeResourceKeys.TextBrush);
        return block;
    }
}
