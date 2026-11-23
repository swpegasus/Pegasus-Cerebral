using System.Windows;
using System.Windows.Controls;
using System.Windows.Automation;
using System.Windows.Data;
using Cerebral.Desktop;
using Cerebral.Desktop.Plugins;

namespace Cerebral.Orchestrator;

public sealed partial class OrchestratorWindow
{
    private readonly Grid desktopPrimaryStatusItems = new() { VerticalAlignment = VerticalAlignment.Center,
        Margin = new Thickness(0, 0, 12, 0), Visibility = Visibility.Collapsed,
        ColumnDefinitions =
        {
            new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
            new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
            new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }
        }
    };
    private FrameworkElement BuildPrimaryStatusControls() => desktopPrimaryStatusItems;
    private void ClearPrimaryStatusControls()
    {
        foreach (var frame in desktopPrimaryStatusItems.Children.OfType<Border>()) frame.Child = null;
        desktopPrimaryStatusItems.Children.Clear(); desktopPrimaryStatusItems.Visibility = Visibility.Collapsed;
    }
    private bool TryMountPrimaryStatusControl(DesktopHostedPanel item)
    {
        if (item.PluginId != "pegasus.resource-pause" || item.Id is not ("resource-status" or "engine-status" or "work-hold-status")) return false;
        if (item.View.Parent is Border previousBorder) previousBorder.Child = null;
        else if (item.View.Parent is ContentControl previousContent) previousContent.Content = null;
        else if (item.View.Parent is Panel previous) previous.Children.Remove(item.View);
        var frame = DesktopMountLayout.StatusItemFrame(item.View);
        // Primary actions use full themed buttons; the compact indicator limit clips their content.
        frame.MaxHeight = 52; frame.Margin = new Thickness(0, 2, 6, 2);
        frame.SetBinding(FrameworkElement.ToolTipProperty, new Binding(nameof(FrameworkElement.ToolTip))
        { Source = item.View, Mode = BindingMode.OneWay, FallbackValue = item.Title, TargetNullValue = item.Title });
        Grid.SetColumn(frame, item.Id switch { "resource-status" => 0, "engine-status" => 1, _ => 2 });
        AutomationProperties.SetAutomationId(frame, "PluginStatusItem-" + item.PluginId + "-" + item.Id);
        AutomationProperties.SetName(frame, item.Title + " plugin status item");
        desktopPrimaryStatusItems.Children.Add(frame); desktopPrimaryStatusItems.Visibility = Visibility.Visible;
        return true;
    }
}
