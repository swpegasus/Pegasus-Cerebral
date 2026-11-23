using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using Cerebral.Desktop.Plugins;

namespace Cerebral.WindowWatchPlugin;

internal sealed partial class WatchWindow
{
    private void ApplyPanelTheme()
    {
        // Do not copy host colors or shadow its live brushes. Standalone previews have
        // no Cerebral resources, so give only this window the suite's Blue White palette.
        if (Application.Current?.TryFindResource(DesktopThemeResourceKeys.TextBrush) is not Brush)
        {
            Resources[SystemColors.ControlBrushKey] = CreateBrush("#F3F6FB");
            Resources[SystemColors.WindowBrushKey] = CreateBrush("#FFFFFF");
            Resources[SystemColors.ControlTextBrushKey] = CreateBrush("#172B4D");
            Resources[SystemColors.WindowTextBrushKey] = CreateBrush("#172B4D");
            Resources[SystemColors.GrayTextBrushKey] = CreateBrush("#52647E");
            Resources[SystemColors.ActiveBorderBrushKey] = CreateBrush("#D5DFEF");
            Resources[SystemColors.HighlightBrushKey] = CreateBrush("#DBE8FF");
            Resources[SystemColors.HighlightTextBrushKey] = CreateBrush("#15356E");
        }
        FontFamily = new FontFamily("Segoe UI");
        FontSize = 13;
        SetResourceReference(BackgroundProperty, SystemColors.ControlBrushKey);
        SetResourceReference(ForegroundProperty, SystemColors.ControlTextBrushKey);

        // These styles are window-local; no host resources or other plugin views change.
        var styles = (ResourceDictionary)XamlReader.Parse("""
            <ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                                xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
              <Style TargetType="Button">
                <Setter Property="Background" Value="{DynamicResource {x:Static SystemColors.WindowBrushKey}}"/>
                <Setter Property="Foreground" Value="{DynamicResource {x:Static SystemColors.ControlTextBrushKey}}"/>
                <Setter Property="BorderBrush" Value="{DynamicResource {x:Static SystemColors.ActiveBorderBrushKey}}"/>
                <Setter Property="BorderThickness" Value="1"/>
                <Setter Property="Padding" Value="7"/>
                <Setter Property="HorizontalContentAlignment" Value="Center"/>
                <Setter Property="VerticalContentAlignment" Value="Center"/>
                <Setter Property="Template">
                  <Setter.Value>
                    <ControlTemplate TargetType="Button">
                      <Border x:Name="Frame" Background="{TemplateBinding Background}"
                              BorderBrush="{TemplateBinding BorderBrush}" BorderThickness="{TemplateBinding BorderThickness}"
                              CornerRadius="7" Padding="{TemplateBinding Padding}">
                        <ContentPresenter HorizontalAlignment="{TemplateBinding HorizontalContentAlignment}"
                                          VerticalAlignment="{TemplateBinding VerticalContentAlignment}"/>
                      </Border>
                      <ControlTemplate.Triggers>
                        <Trigger Property="IsMouseOver" Value="True">
                          <Setter TargetName="Frame" Property="Background" Value="{DynamicResource {x:Static SystemColors.HighlightBrushKey}}"/>
                          <Setter Property="Foreground" Value="{DynamicResource {x:Static SystemColors.HighlightTextBrushKey}}"/>
                        </Trigger>
                        <Trigger Property="IsKeyboardFocused" Value="True">
                          <Setter TargetName="Frame" Property="BorderBrush" Value="{DynamicResource {x:Static SystemColors.HighlightTextBrushKey}}"/>
                          <Setter TargetName="Frame" Property="BorderThickness" Value="2"/>
                        </Trigger>
                        <Trigger Property="IsPressed" Value="True">
                          <Setter TargetName="Frame" Property="Opacity" Value="0.7"/>
                        </Trigger>
                        <Trigger Property="IsEnabled" Value="False">
                          <Setter Property="Opacity" Value="0.4"/>
                        </Trigger>
                      </ControlTemplate.Triggers>
                    </ControlTemplate>
                  </Setter.Value>
                </Setter>
                <Style.Triggers>
                  <Trigger Property="Tag" Value="active">
                    <Setter Property="Background" Value="{DynamicResource {x:Static SystemColors.HighlightBrushKey}}"/>
                    <Setter Property="Foreground" Value="{DynamicResource {x:Static SystemColors.HighlightTextBrushKey}}"/>
                  </Trigger>
                </Style.Triggers>
              </Style>
              <Style TargetType="TextBox">
                <Setter Property="Background" Value="{DynamicResource {x:Static SystemColors.WindowBrushKey}}"/>
                <Setter Property="Foreground" Value="{DynamicResource {x:Static SystemColors.ControlTextBrushKey}}"/>
                <Setter Property="BorderBrush" Value="{DynamicResource {x:Static SystemColors.ActiveBorderBrushKey}}"/>
                <Setter Property="BorderThickness" Value="1"/>
                <Setter Property="Padding" Value="9"/>
                <Setter Property="SelectionBrush" Value="{DynamicResource {x:Static SystemColors.HighlightBrushKey}}"/>
                <Setter Property="SelectionTextBrush" Value="{DynamicResource {x:Static SystemColors.HighlightTextBrushKey}}"/>
                <Setter Property="Template">
                  <Setter.Value>
                    <ControlTemplate TargetType="TextBox">
                      <Border x:Name="Frame" Background="{TemplateBinding Background}"
                              BorderBrush="{TemplateBinding BorderBrush}" BorderThickness="{TemplateBinding BorderThickness}"
                              CornerRadius="7">
                        <ScrollViewer x:Name="PART_ContentHost" Margin="{TemplateBinding Padding}" Focusable="False"
                                      HorizontalScrollBarVisibility="{TemplateBinding HorizontalScrollBarVisibility}"
                                      VerticalScrollBarVisibility="{TemplateBinding VerticalScrollBarVisibility}"/>
                      </Border>
                      <ControlTemplate.Triggers>
                        <Trigger Property="IsKeyboardFocusWithin" Value="True">
                          <Setter TargetName="Frame" Property="BorderBrush" Value="{DynamicResource {x:Static SystemColors.HighlightTextBrushKey}}"/>
                        </Trigger>
                        <Trigger Property="IsEnabled" Value="False">
                          <Setter Property="Opacity" Value="0.55"/>
                        </Trigger>
                      </ControlTemplate.Triggers>
                    </ControlTemplate>
                  </Setter.Value>
                </Setter>
              </Style>
            </ResourceDictionary>
            """);
        Resources.MergedDictionaries.Add(styles);
    }

    private static SolidColorBrush CreateBrush(string color)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
        brush.Freeze();
        return brush;
    }
}
