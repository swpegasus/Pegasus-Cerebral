using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using Cerebral.Desktop.Plugins;

namespace Cerebral.Desktop;

/// <summary>Keeps plugin action feedback beside the exact text entry that owns the action.</summary>
public sealed class TextEntryActionIndicator
{
    private readonly Dictionary<(string PluginId, string ActionId), (Button Button, object? DefaultContent)> buttons = [];
    private readonly Dictionary<(string PluginId, string ActionId), DesktopTextEntryActivity> activities = [];
    private readonly Border[] levelBars;
    private readonly StackPanel levelMeter;

    public FrameworkElement View { get; }

    public TextBlock Status { get; } = new()
    {
        FontSize = 12,
        Foreground = Ui.Muted,
        TextWrapping = TextWrapping.Wrap,
        Visibility = Visibility.Collapsed,
        Margin = new Thickness(0, 5, 0, 0)
    };

    /// <summary>
    /// True while a plugin is still producing or inserting text for this entry. A host can use this
    /// as a submission barrier so it never freezes a draft before the asynchronous edit completes.
    /// </summary>
    public bool HasPendingAction => activities.Values.Any(IsPending);

    /// <summary>The most recent useful status for a pending edit, when the plugin supplied one.</summary>
    public string? PendingMessage => activities.Values.Where(IsPending).Select(value => value.Message)
        .LastOrDefault(value => !string.IsNullOrWhiteSpace(value));

    public TextEntryActionIndicator(string entryId)
    {
        levelMeter = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(4, 0, 5, 0),
            Visibility = Visibility.Collapsed
        };
        levelBars = Enumerable.Range(0, 8).Select(index =>
        {
            var bar = new Border
            {
                Width = 3,
                Height = 4 + index * 1.5,
                Margin = new Thickness(1, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Bottom,
                Background = Brushes.SlateGray,
                Opacity = .25
            };
            levelMeter.Children.Add(bar);
            return bar;
        }).ToArray();
        View = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { levelMeter, Status }
        };
        AutomationProperties.SetAutomationId(Status, "TextEntryActivity-" + entryId);
        AutomationProperties.SetName(Status, "Text-entry action status");
        AutomationProperties.SetLiveSetting(Status, AutomationLiveSetting.Polite);
        AutomationProperties.SetAutomationId(levelMeter, "TextEntryInputLevel-" + entryId);
        AutomationProperties.SetName(levelMeter, "Live microphone level");
    }

    public void Register(string pluginId, string actionId, Button button)
    {
        buttons[(pluginId, actionId)] = (button, button.Content);
    }

    public void Report(DesktopTextEntryActivity activity)
    {
        if (!buttons.TryGetValue((activity.PluginId, activity.ActionId), out var action)) return;
        activities[(activity.PluginId, activity.ActionId)] = activity;
        action.Button.Content = activity.ButtonLabel ?? (activity.Active ? "Stop " + action.DefaultContent : action.DefaultContent);
        action.Button.IsEnabled = activity.State is not (DesktopTextEntryActivityState.Starting or DesktopTextEntryActivityState.Stopping);
        Status.Text = activity.Message;
        Status.Foreground = activity.State == DesktopTextEntryActivityState.Error
            ? Ui.ErrorBorder
            : activity.Active || activity.State == DesktopTextEntryActivityState.Result ? Brushes.Teal : Ui.Muted;
        Status.Visibility = string.IsNullOrWhiteSpace(activity.Message) ? Visibility.Collapsed : Visibility.Visible;
        ShowLevel(activity.Active && activity.State == DesktopTextEntryActivityState.Listening ? activity.InputLevel : null);
    }

    public void Reset()
    {
        foreach (var action in buttons.Values)
        {
            action.Button.Content = action.DefaultContent;
            action.Button.IsEnabled = true;
        }
        buttons.Clear();
        activities.Clear();
        Status.ClearValue(TextBlock.TextProperty);
        Status.Foreground = Ui.Muted;
        Status.Visibility = Visibility.Collapsed;
        ShowLevel(null);
    }

    private static bool IsPending(DesktopTextEntryActivity activity) => activity.Active ||
        activity.State is DesktopTextEntryActivityState.Starting or DesktopTextEntryActivityState.Stopping;

    private void ShowLevel(double? value)
    {
        if (value is null || !double.IsFinite(value.Value))
        {
            levelMeter.Visibility = Visibility.Collapsed;
            foreach (var bar in levelBars) { bar.Opacity = .25; bar.Background = Brushes.SlateGray; }
            return;
        }
        var level = Math.Clamp(value.Value, 0, 1);
        var active = Math.Max(1, (int)Math.Ceiling(level * levelBars.Length));
        var color = level >= .99 ? Brushes.OrangeRed : level >= .12 ? Brushes.MediumSeaGreen : Brushes.SlateGray;
        for (var index = 0; index < levelBars.Length; index++)
        {
            levelBars[index].Background = index < active ? color : Brushes.SlateGray;
            levelBars[index].Opacity = index < active ? 1 : .2;
        }
        levelMeter.ToolTip = $"Processed microphone level {level * 100:0}%";
        levelMeter.Visibility = Visibility.Visible;
    }
}
