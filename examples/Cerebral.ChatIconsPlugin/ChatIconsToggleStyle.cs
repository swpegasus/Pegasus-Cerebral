using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;

namespace Cerebral.ChatIconsPlugin;

/// <summary>Stock toggle templates do not consume the host's button palette. Keep their text/background paired.</summary>
internal static class ChatIconsToggleStyle
{
    private static readonly Style Shared = Create();
    internal static void Apply(ToggleButton button) => button.Style = Shared;

    private static Style Create()
    {
        var style = new Style(typeof(ToggleButton));
        style.Setters.Add(new Setter(Control.BackgroundProperty, new DynamicResourceExtension(SystemColors.ControlBrushKey)));
        style.Setters.Add(new Setter(Control.ForegroundProperty, new DynamicResourceExtension(SystemColors.ControlTextBrushKey)));
        style.Setters.Add(new Setter(Control.BorderBrushProperty, new DynamicResourceExtension(SystemColors.ActiveBorderBrushKey)));
        style.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(1)));
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetBinding(Border.BackgroundProperty, Parent(nameof(Control.Background)));
        border.SetBinding(Border.BorderBrushProperty, Parent(nameof(Control.BorderBrush)));
        border.SetBinding(Border.BorderThicknessProperty, Parent(nameof(Control.BorderThickness)));
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(3));
        var content = new FrameworkElementFactory(typeof(ContentPresenter));
        content.SetBinding(ContentPresenter.ContentProperty, Parent(nameof(ContentControl.Content)));
        content.SetBinding(FrameworkElement.MarginProperty, Parent(nameof(Control.Padding)));
        content.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        content.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        content.SetValue(ContentPresenter.RecognizesAccessKeyProperty, true);
        border.AppendChild(content);
        style.Setters.Add(new Setter(Control.TemplateProperty, new ControlTemplate(typeof(ToggleButton)) { VisualTree = border }));
        var selected = new Trigger { Property = ToggleButton.IsCheckedProperty, Value = true };
        selected.Setters.Add(new Setter(Control.BackgroundProperty, new DynamicResourceExtension(SystemColors.HighlightBrushKey)));
        selected.Setters.Add(new Setter(Control.ForegroundProperty, new DynamicResourceExtension(SystemColors.HighlightTextBrushKey)));
        style.Triggers.Add(selected);
        var focus = new Trigger { Property = UIElement.IsKeyboardFocusedProperty, Value = true };
        focus.Setters.Add(new Setter(Control.BorderBrushProperty, new DynamicResourceExtension(SystemColors.HighlightBrushKey)));
        focus.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(2)));
        style.Triggers.Add(focus);
        var disabled = new Trigger { Property = UIElement.IsEnabledProperty, Value = false };
        disabled.Setters.Add(new Setter(UIElement.OpacityProperty, .55));
        style.Triggers.Add(disabled);
        return style;
    }
    private static Binding Parent(string property) => new(property) { RelativeSource = RelativeSource.TemplatedParent };
}
