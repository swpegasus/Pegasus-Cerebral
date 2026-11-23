using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Cerebral.Desktop;

/// <summary>The shared Cerebral identity; stable application resource names use the same wing-and-network mark.</summary>
public static class Branding
{
    public const string ApplicationName = "Pegasus Cerebral";
    public const string ConfigurationName = "Pegasus Cerebral Configuration";

    // Role identifiers stay stable in paths and protocols; presentation names can change independently.
    public static string DisplayName(string application) => application.ToLowerInvariant() switch
    {
        "integrator" => ConfigurationName,
        "worker" => "Pegasus Cerebral Worker",
        "orchestrator" => ApplicationName,
        _ => application.StartsWith("Pegasus ", StringComparison.Ordinal) ? application : $"Pegasus Cerebral {application}"
    };

    public static ImageSource IconFor(string application)
    {
        var name = application.ToLowerInvariant() switch
        {
            "integrator" => "integrator",
            "worker" => "worker",
            _ => "orchestrator"
        };
        var decoder = new IconBitmapDecoder(new Uri($"pack://application:,,,/Cerebral.Desktop;component/Branding/cerebral-{name}.ico", UriKind.Absolute),
            BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        var source = decoder.Frames.OrderByDescending(frame => frame.PixelWidth).First();
        source.Freeze();
        return source;
    }

    /// <summary>Shared loading artwork used by desktop hosts without relying on an install path.</summary>
    public static ImageSource SplashArtwork()
    {
        var source = new BitmapImage(new Uri(
            "pack://application:,,,/Cerebral.Desktop;component/Branding/cerebral-splash-sidebar.png",
            UriKind.Absolute));
        source.Freeze();
        return source;
    }

    /// <summary>Transparent shape mask tinted by the current theme at the point of use.</summary>
    public static ImageSource WatermarkMask()
    {
        var source = new BitmapImage(new Uri(
            "pack://application:,,,/Cerebral.Desktop;component/Branding/cerebral-watermark.png",
            UriKind.Absolute));
        source.Freeze();
        return source;
    }

    public static FrameworkElement Header(string application, string description, bool compact = false)
    {
        var layout = new Grid { Margin = new Thickness(0) };
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var iconSize = compact ? 40 : 48;
        var image = new Image
        {
            Source = IconFor(application), Width = iconSize, Height = iconSize,
            Margin = new Thickness(0, 0, compact ? 12 : 16, 0), VerticalAlignment = VerticalAlignment.Center
        };
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
        AutomationProperties.SetName(image, DisplayName(application));
        layout.Children.Add(image);
        var words = new StackPanel();
        var title = Ui.Text(DisplayName(application), compact ? 21 : 24);
        title.FontWeight = FontWeights.SemiBold; title.Margin = new Thickness(0, 0, 0, compact ? 1 : 3);
        var detail = Ui.Text(description, compact ? 12 : 13, Ui.Muted); detail.Margin = new Thickness(0);
        words.Children.Add(title); words.Children.Add(detail);
        Grid.SetColumn(words, 1); layout.Children.Add(words);
        return layout;
    }
}
