using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;

namespace Cerebral.Orchestrator;

internal sealed class ComposerSuggestionSurface : Grid
{
    private readonly Grid overlay = new() { IsHitTestVisible = false, Focusable = false };
    private readonly TextBlock ghost;
    internal ComposerSuggestionSurface(TextBox composer, Brush muted)
    {
        ClipToBounds = true;
        Children.Add(composer);
        // Canvas contributes no desired height: a long suggestion cannot resize the actual composer.
        var canvas = new Canvas { IsHitTestVisible = false, Focusable = false, ClipToBounds = true };
        canvas.Children.Add(overlay); Children.Add(canvas);
        ghost = new TextBlock
        {
            Foreground = muted, Opacity = .7, TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.CharacterEllipsis,
            FontFamily = composer.FontFamily, FontSize = composer.FontSize, VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(composer.Padding.Left + composer.BorderThickness.Left,
                composer.Padding.Top + composer.BorderThickness.Top, composer.Padding.Right + composer.BorderThickness.Right, 22)
        };
        overlay.Children.Add(ghost);
        overlay.Children.Add(new TextBlock
        {
            Text = "Tab to insert", Foreground = muted, FontSize = 11, HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(8, 0, 8, 5)
        });
        AutomationProperties.SetAutomationId(overlay, "ComposerSuggestion");
        SizeChanged += (_, _) =>
        {
            overlay.Width = ActualWidth; overlay.Height = ActualHeight;
            ghost.MaxHeight = Math.Max(0, ActualHeight - ghost.Margin.Top - ghost.Margin.Bottom);
        };
        Show(null);
    }
    internal void Show(string? text)
    {
        ghost.Text = text ?? "";
        overlay.Visibility = text is null ? Visibility.Collapsed : Visibility.Visible;
        AutomationProperties.SetName(overlay, text is null ? "" : "Suggested next message. Press Tab to insert without sending.");
    }
}
