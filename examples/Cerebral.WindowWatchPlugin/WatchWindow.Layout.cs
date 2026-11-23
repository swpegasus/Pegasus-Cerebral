using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Shapes;

namespace Cerebral.WindowWatchPlugin;

internal sealed partial class WatchWindow
{
    private const double ReadingMinHeight = 310;
    private const double FocusMinHeight = 230;
    private const double SetupMinHeight = 390;
    private readonly Button optionsButton = new();
    private readonly Button focusButton = new();
    private readonly StackPanel contextCard = new() { Margin = new Thickness(0, 0, 0, 9) };
    private readonly TextBlock modeCaption = new() { FontSize = 11, Margin = new Thickness(0, 3, 0, 0) };
    private bool focusMode;
    private double focusHeightAdjustment;
    private readonly Border optionsCard = new() { Visibility = Visibility.Collapsed };
    private readonly TextBlock targetCaption = new()
    {
        TextTrimming = TextTrimming.CharacterEllipsis,
        FontSize = 14,
        FontWeight = FontWeights.SemiBold,
        VerticalAlignment = VerticalAlignment.Center,
        Margin = new Thickness(0, 0, 8, 0)
    };
    private double compactHeight;

    private FrameworkElement BuildLayout()
    {
        var root = new Grid { Margin = new Thickness(14) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        ConfigureIcon(analyze, "WindowWatch.Snapshot", "Analyze now",
            "M3,7 H7 L9,4 H15 L17,7 H21 V19 H3 Z M16,13 A4,4 0 1 1 8,13 A4,4 0 1 1 16,13");
        ConfigureIcon(start, "WindowWatch.Auto", "Start auto", "M8,4 L20,12 L8,20 Z");
        ConfigureIcon(stop, "WindowWatch.Pause", "Pause auto", "M8,5 V19 M16,5 V19");
        ConfigureIcon(optionsButton, "WindowWatch.Options", "Show options",
            "M9,3 H15 L16,6 H19 L22,11 L20,13 V15 L21,17 L18,21 L15,20 L12,22 L9,20 L6,21 L3,17 L4,15 V13 L2,11 L5,6 H8 Z M15.5,12 A3.5,3.5 0 1 1 8.5,12 A3.5,3.5 0 1 1 15.5,12");
        ConfigureIcon(focusButton, "WindowWatch.Focus", "Focus on response",
            "M4,9 V4 H9 M15,4 H20 V9 M20,15 V20 H15 M9,20 H4 V15");
        ConfigureIcon(refresh, "WindowWatch.Refresh", "Refresh windows",
            "M20,8 A9,9 0 1 0 21,15 M20,3 V8 H15");
        ConfigureIcon(clear, "WindowWatch.Clear", "Clear replies",
            "M4,6 H20 M9,6 V3 H15 V6 M6,6 L7,21 H17 L18,6 M10,10 V17 M14,10 V17");
        optionsButton.Click += (_, _) => SetOptionsVisible(optionsCard.Visibility != Visibility.Visible);
        focusButton.Click += (_, _) => SetFocusMode(!focusMode);

        var toolbar = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var action in new[] { analyze, start, stop, focusButton, optionsButton })
            toolbar.Children.Add(action);

        var header = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 0, 0, 9) };
        DockPanel.SetDock(toolbar, Dock.Right);
        header.Children.Add(toolbar);
        var identity = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        identity.Children.Add(targetCaption);
        modeCaption.SetResourceReference(TextBlock.ForegroundProperty, SystemColors.GrayTextBrushKey);
        identity.Children.Add(modeCaption);
        header.Children.Add(identity);
        root.Children.Add(header);

        optionsCard.Child = BuildOptions();
        optionsCard.CornerRadius = new CornerRadius(9);
        optionsCard.Padding = new Thickness(10, 6, 10, 8);
        optionsCard.Margin = new Thickness(0, 0, 0, 9);
        optionsCard.SetResourceReference(Border.BackgroundProperty, SystemColors.WindowBrushKey);
        optionsCard.SetResourceReference(Border.BorderBrushProperty, SystemColors.ActiveBorderBrushKey);
        optionsCard.BorderThickness = new Thickness(1);
        AutomationProperties.SetAutomationId(optionsCard, "WindowWatch.Setup");
        Grid.SetRow(optionsCard, 1);
        root.Children.Add(optionsCard);

        prompt.Height = 58;
        prompt.ToolTip = "What should the helper look for? This prompt is kept when you fold the panel.";
        AddField(contextCard, "Context prompt", prompt);
        AutomationProperties.SetAutomationId(contextCard, "WindowWatch.Context");
        Grid.SetRow(contextCard, 2);
        root.Children.Add(contextCard);

        var response = new DockPanel { LastChildFill = true };
        var responseLabel = FieldLabel("Latest response");
        DockPanel.SetDock(responseLabel, Dock.Top);
        response.Children.Add(responseLabel);
        AutomationProperties.SetName(output, "Latest AI response");
        output.SetResourceReference(BackgroundProperty, SystemColors.WindowBrushKey);
        var responseBody = new Grid();
        responseBody.Children.Add(output);
        var emptyHint = new TextBlock
        {
            Text = "Your screen analysis will appear here.",
            Margin = new Thickness(12), TextWrapping = TextWrapping.Wrap,
            IsHitTestVisible = false
        };
        emptyHint.SetResourceReference(TextBlock.ForegroundProperty, SystemColors.GrayTextBrushKey);
        var hintStyle = new Style(typeof(TextBlock));
        hintStyle.Setters.Add(new Setter(VisibilityProperty, Visibility.Collapsed));
        hintStyle.Triggers.Add(new DataTrigger
        {
            Binding = new Binding(nameof(TextBox.Text)) { Source = output }, Value = "",
            Setters = { new Setter(VisibilityProperty, Visibility.Visible) }
        });
        emptyHint.Style = hintStyle;
        responseBody.Children.Add(emptyHint);
        response.Children.Add(responseBody);
        Grid.SetRow(response, 3);
        root.Children.Add(response);

        var footer = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 8, 0, 0) };
        var timerBadge = new Border
        {
            Child = countdown, CornerRadius = new CornerRadius(6),
            Padding = new Thickness(7, 3, 7, 3), Margin = new Thickness(9, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Top
        };
        timerBadge.SetResourceReference(Border.BackgroundProperty, SystemColors.WindowBrushKey);
        var badgeStyle = new Style(typeof(Border));
        badgeStyle.Triggers.Add(new DataTrigger
        {
            Binding = new Binding(nameof(TextBlock.Text)) { Source = countdown }, Value = "",
            Setters = { new Setter(VisibilityProperty, Visibility.Collapsed) }
        });
        timerBadge.Style = badgeStyle;
        DockPanel.SetDock(timerBadge, Dock.Right);
        AutomationProperties.SetName(countdown, "Capture timer");
        countdown.FontSize = 12;
        countdown.FontWeight = FontWeights.SemiBold;
        status.Margin = new Thickness(0);
        status.FontSize = 12;
        status.SetResourceReference(TextBlock.ForegroundProperty, SystemColors.GrayTextBrushKey);
        AutomationProperties.SetLiveSetting(status, AutomationLiveSetting.Polite);
        footer.Children.Add(timerBadge);
        footer.Children.Add(status);
        Grid.SetRow(footer, 4);
        root.Children.Add(footer);
        return root;
    }

    private FrameworkElement BuildOptions()
    {
        var fields = new StackPanel();
        var targetRow = new DockPanel();
        DockPanel.SetDock(refresh, Dock.Right);
        refresh.Margin = new Thickness(6, 0, 0, 0);
        targetRow.Children.Add(refresh);
        targetRow.Children.Add(targets);
        AddField(fields, "Target window", targetRow, targets);
        endpoint.ToolTip = "Example: http://127.0.0.1:8080/v1/chat/completions";
        AddField(fields, "Local vision endpoint", endpoint);
        AddField(fields, "Vision model name", model);

        AutomationProperties.SetName(seconds, "Auto interval in seconds");
        fields.Children.Add(Row(FieldLabel("Interval (seconds)"), seconds, onlyChanges));
        fields.Children.Add(Row(textDetail, showOutline, stayOnTop));
        AutomationProperties.SetName(replyLimit, "Reply limit");
        fields.Children.Add(Row(keepReplies, FieldLabel("Reply limit"), replyLimit, clear));
        var hint = FieldLabel("Pause other AI work before starting. The selected local endpoint receives each analyzed snapshot.");
        hint.TextWrapping = TextWrapping.Wrap;
        fields.Children.Add(hint);
        return new ScrollViewer
        {
            Content = fields,
            MaxHeight = 208,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        };
    }

    private void SetOptionsVisible(bool visible)
    {
        if (visible && focusMode) SetFocusMode(false);
        if (visible == (optionsCard.Visibility == Visibility.Visible)) return;
        MinHeight = visible ? SetupMinHeight : focusMode ? FocusMinHeight : ReadingMinHeight;
        if (visible)
        {
            // Height is the latest requested size; ActualHeight can still describe the
            // previous view when Options and Focus switch in one dispatcher turn.
            compactHeight = double.IsNaN(Height) ? ActualHeight : Height;
            var room = SystemParameters.WorkArea.Height - 36;
            Height = Math.Max(compactHeight, Math.Min(compactHeight + 230, room));
        }
        else if (compactHeight > 0)
            Height = compactHeight;
        optionsCard.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        optionsButton.Tag = visible ? "active" : null;
        SetActionLabel(optionsButton, visible ? "Hide options" : "Show options");
    }

    private void SetFocusMode(bool focused)
    {
        if (focusMode == focused) return;
        if (focused)
        {
            SetOptionsVisible(false);
            MinHeight = FocusMinHeight;
            var height = double.IsNaN(Height) ? ActualHeight : Height;
            var contextHeight = contextCard.ActualHeight > 0
                ? contextCard.ActualHeight + contextCard.Margin.Bottom : 87;
            focusHeightAdjustment = Math.Min(contextHeight, Math.Max(0, height - MinHeight));
            contextCard.Visibility = Visibility.Collapsed;
            Height = height - focusHeightAdjustment;
        }
        else
        {
            contextCard.Visibility = Visibility.Visible;
            MinHeight = ReadingMinHeight;
            Height = Math.Max(MinHeight, Height + focusHeightAdjustment);
        }
        focusMode = focused;
        focusButton.Tag = focused ? "active" : null;
        SetActionLabel(focusButton, focused ? "Show context prompt" : "Focus on response");
    }

    private static void ConfigureIcon(Button button, string id, string label, string path)
    {
        button.Width = 35;
        button.Height = 34;
        button.Padding = new Thickness(6);
        button.Margin = new Thickness(4, 0, 0, 0);
        var glyph = new Path
        {
            Data = Geometry.Parse(path),
            Stretch = Stretch.Uniform,
            StrokeThickness = 1.7,
            StrokeLineJoin = PenLineJoin.Round,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            Width = 17,
            Height = 17
        };
        glyph.SetBinding(Shape.StrokeProperty, new Binding(nameof(Button.Foreground)) { Source = button });
        button.Content = glyph;
        AutomationProperties.SetAutomationId(button, id);
        SetActionLabel(button, label);
    }

    private static void SetActionLabel(Button button, string label)
    {
        AutomationProperties.SetName(button, label);
        button.ToolTip = label;
    }

    private static FrameworkElement Row(params FrameworkElement[] items)
    {
        var row = new WrapPanel { Margin = new Thickness(0, 5, 0, 0) };
        foreach (var item in items)
        {
            item.Margin = new Thickness(0, 0, 8, 3);
            item.VerticalAlignment = VerticalAlignment.Center;
            row.Children.Add(item);
        }
        return row;
    }

    private static TextBlock FieldLabel(string text)
    {
        var label = new TextBlock { Text = text, FontSize = 12, Margin = new Thickness(0, 3, 0, 3) };
        label.SetResourceReference(TextBlock.ForegroundProperty, SystemColors.GrayTextBrushKey);
        return label;
    }

    private static void AddField(Panel panel, string label, FrameworkElement field, FrameworkElement? named = null)
    {
        panel.Children.Add(FieldLabel(label));
        AutomationProperties.SetName(named ?? field, label);
        panel.Children.Add(field);
    }
}
