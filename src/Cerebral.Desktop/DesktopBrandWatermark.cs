using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace Cerebral.Desktop;

/// <summary>A decorative base layer; ordinary opaque plugin views can cover it without owning the artwork.</summary>
public static class DesktopBrandWatermark
{
    // A native plugin can hide the hosting window's decoration with a WPF resource override:
    // Window.GetWindow(view).Resources[VisibilityResourceKey] = Visibility.Collapsed.
    // This is a presentation resource, not a new plugin SDK or stored color-preset token.
    public const string VisibilityResourceKey = "Cerebral.Branding.WatermarkVisibility";

    public static FrameworkElement Create()
    {
        var layer = new Grid
        {
            IsHitTestVisible = false, Focusable = false, ClipToBounds = true,
            Margin = new Thickness(40), UseLayoutRounding = true
        };
        layer.SetResourceReference(UIElement.VisibilityProperty, VisibilityResourceKey);
        AutomationProperties.SetAutomationId(layer, "ChatBrandWatermark");
        var mask = new ImageBrush(Branding.WatermarkMask())
        {
            AlignmentX = AlignmentX.Center, AlignmentY = AlignmentY.Center, Stretch = Stretch.Uniform
        };
        mask.Freeze();
        // Matching the fill to the live surface leaves only a shallow edge relief. The blue
        // body tint remains faint enough for transcript text and transparent plugin content.
        layer.Children.Add(Relief(mask, highlight: false));
        layer.Children.Add(Relief(mask, highlight: true));
        layer.Children.Add(new Border { Background = Ui.Accent, OpacityMask = mask, Opacity = .018 });
        return layer;
    }

    private static Border Relief(Brush mask, bool highlight)
    {
        var effect = new DropShadowEffect
        {
            BlurRadius = 2.4, ShadowDepth = 1.4, Direction = highlight ? 135 : 315,
            Opacity = highlight ? .22 : .16, RenderingBias = RenderingBias.Quality
        };
        BindingOperations.SetBinding(effect, DropShadowEffect.ColorProperty,
            new Binding(nameof(SolidColorBrush.Color))
            {
                Source = Ui.Surface, Converter = new ReliefColorConverter(highlight), Mode = BindingMode.OneWay
            });
        return new Border { Background = Ui.Surface, OpacityMask = mask, Effect = effect };
    }

    private sealed class ReliefColorConverter(bool highlight) : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is not Color surface) return DependencyProperty.UnsetValue;
            byte Shift(byte channel) => (byte)Math.Round(highlight
                ? channel + (255 - channel) * .55 : channel * .45);
            return Color.FromRgb(Shift(surface.R), Shift(surface.G), Shift(surface.B));
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
            Binding.DoNothing;
    }
}
