using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;

namespace Cerebral.Orchestrator;

/// <summary>Resources stay inside the chat pane; no application or plugin theme is replaced.</summary>
internal static class ChatDockAppearance
{
    internal static void Apply(FrameworkElement pane)
    {
        var resources = (ResourceDictionary)XamlReader.Parse("""
<ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
  <Style x:Key="ChatDockBar" TargetType="{x:Type Expander}">
    <Setter Property="HorizontalContentAlignment" Value="Stretch"/>
    <Setter Property="VerticalContentAlignment" Value="Stretch"/>
    <Setter Property="Margin" Value="0"/>
    <Setter Property="Foreground" Value="#F2F5F8"/>
    <Setter Property="Template">
      <Setter.Value>
        <ControlTemplate TargetType="{x:Type Expander}">
          <Grid ClipToBounds="True">
            <Grid.RowDefinitions><RowDefinition Height="Auto"/><RowDefinition Height="*"/></Grid.RowDefinitions>
            <ToggleButton x:Name="HeaderSite" Height="28" Padding="6,0" BorderThickness="0,0,0,1"
                BorderBrush="#707A86" Background="#424A54" Foreground="#F2F5F8"
                HorizontalContentAlignment="Stretch" VerticalContentAlignment="Center"
                Content="{TemplateBinding Header}" ContentTemplate="{TemplateBinding HeaderTemplate}"
                IsChecked="{Binding IsExpanded, RelativeSource={RelativeSource TemplatedParent}, Mode=TwoWay}">
              <ToggleButton.Template>
                <ControlTemplate TargetType="{x:Type ToggleButton}">
                  <Border x:Name="Bar" Background="{TemplateBinding Background}" BorderBrush="{TemplateBinding BorderBrush}" BorderThickness="{TemplateBinding BorderThickness}" Padding="{TemplateBinding Padding}">
                    <Grid><Grid.ColumnDefinitions><ColumnDefinition Width="18"/><ColumnDefinition Width="*"/></Grid.ColumnDefinitions>
                      <TextBlock x:Name="Arrow" Text="▾" Foreground="{TemplateBinding Foreground}" FontSize="12" VerticalAlignment="Center"/>
                      <ContentPresenter Grid.Column="1" ContentSource="Content" VerticalAlignment="Center" HorizontalAlignment="Stretch" RecognizesAccessKey="True"/>
                    </Grid>
                  </Border>
                  <ControlTemplate.Triggers>
                    <Trigger Property="IsChecked" Value="True"><Setter TargetName="Arrow" Property="Text" Value="▴"/><Setter TargetName="Bar" Property="Background" Value="#505B68"/></Trigger>
                    <Trigger Property="IsMouseOver" Value="True"><Setter TargetName="Bar" Property="Background" Value="#596574"/></Trigger>
                    <Trigger Property="IsKeyboardFocused" Value="True"><Setter TargetName="Bar" Property="BorderBrush" Value="#F2F5F8"/><Setter TargetName="Bar" Property="BorderThickness" Value="1"/></Trigger>
                  </ControlTemplate.Triggers>
                </ControlTemplate>
              </ToggleButton.Template>
            </ToggleButton>
            <ContentPresenter x:Name="Body" Grid.Row="1" ContentSource="Content" Visibility="Collapsed"
                HorizontalAlignment="{TemplateBinding HorizontalContentAlignment}" VerticalAlignment="{TemplateBinding VerticalContentAlignment}"/>
          </Grid>
          <ControlTemplate.Triggers><Trigger Property="IsExpanded" Value="True"><Setter TargetName="Body" Property="Visibility" Value="Visible"/></Trigger></ControlTemplate.Triggers>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>
  <Style TargetType="{x:Type ScrollBar}">
    <Setter Property="Background" Value="#263442"/>
    <Setter Property="Width" Value="15"/>
    <Setter Property="MinWidth" Value="15"/>
    <Setter Property="Template">
      <Setter.Value>
        <ControlTemplate TargetType="{x:Type ScrollBar}">
          <Border Background="#263442" BorderBrush="#65778B" BorderThickness="1">
            <Track x:Name="PART_Track" IsDirectionReversed="True" Orientation="{TemplateBinding Orientation}" Focusable="False">
              <Track.DecreaseRepeatButton><RepeatButton x:Name="PageBefore" Command="{x:Static ScrollBar.PageUpCommand}" Opacity="0" Focusable="False"/></Track.DecreaseRepeatButton>
              <Track.Thumb><Thumb x:Name="ScrollThumb" Background="#9AABBE" MinHeight="28" MinWidth="10">
                <Thumb.Template><ControlTemplate TargetType="{x:Type Thumb}"><Border x:Name="Grip" Background="{TemplateBinding Background}" CornerRadius="3" Margin="2"/><ControlTemplate.Triggers><Trigger Property="IsMouseOver" Value="True"><Setter TargetName="Grip" Property="Background" Value="#D3DEEA"/></Trigger><Trigger Property="IsDragging" Value="True"><Setter TargetName="Grip" Property="Background" Value="#F2F5F8"/></Trigger></ControlTemplate.Triggers></ControlTemplate></Thumb.Template>
              </Thumb></Track.Thumb>
              <Track.IncreaseRepeatButton><RepeatButton x:Name="PageAfter" Command="{x:Static ScrollBar.PageDownCommand}" Opacity="0" Focusable="False"/></Track.IncreaseRepeatButton>
            </Track>
          </Border>
          <ControlTemplate.Triggers>
            <Trigger Property="Orientation" Value="Horizontal">
              <Setter Property="Width" Value="Auto"/><Setter Property="MinWidth" Value="0"/><Setter Property="Height" Value="15"/><Setter Property="MinHeight" Value="15"/>
              <Setter TargetName="PART_Track" Property="IsDirectionReversed" Value="False"/>
              <Setter TargetName="ScrollThumb" Property="MinWidth" Value="28"/><Setter TargetName="ScrollThumb" Property="MinHeight" Value="10"/>
              <Setter TargetName="PageBefore" Property="Command" Value="{x:Static ScrollBar.PageLeftCommand}"/><Setter TargetName="PageAfter" Property="Command" Value="{x:Static ScrollBar.PageRightCommand}"/>
            </Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>
  <Style TargetType="{x:Type DataGridColumnHeader}">
    <Setter Property="Background" Value="#424A54"/><Setter Property="Foreground" Value="#F2F5F8"/>
    <Setter Property="Padding" Value="8,4"/><Setter Property="BorderBrush" Value="#708094"/><Setter Property="BorderThickness" Value="0,0,1,1"/>
    <Setter Property="FontWeight" Value="SemiBold"/>
  </Style>
  <Style TargetType="{x:Type DataGridRow}">
    <Setter Property="Background" Value="#1A2837"/><Setter Property="Foreground" Value="#F2F5F8"/><Setter Property="MinHeight" Value="25"/>
    <Style.Triggers><Trigger Property="IsSelected" Value="True"><Setter Property="Background" Value="#405A77"/><Setter Property="Foreground" Value="#FFFFFF"/></Trigger></Style.Triggers>
  </Style>
  <Style TargetType="{x:Type DataGridCell}">
    <Setter Property="Foreground" Value="#F2F5F8"/><Setter Property="Padding" Value="7,3"/><Setter Property="BorderBrush" Value="#526479"/><Setter Property="BorderThickness" Value="0,0,1,1"/>
    <Style.Triggers><Trigger Property="IsSelected" Value="True"><Setter Property="Background" Value="#405A77"/><Setter Property="Foreground" Value="#FFFFFF"/></Trigger></Style.Triggers>
  </Style>
</ResourceDictionary>
""");
        pane.Resources.MergedDictionaries.Add(resources);
    }

    internal static void Configure(Expander panel, FrameworkElement pane)
    {
        panel.Style = (Style)pane.FindResource("ChatDockBar");
    }
}
