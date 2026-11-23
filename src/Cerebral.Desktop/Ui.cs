using System.IO;
using System.ComponentModel;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using Microsoft.Win32;
using Cerebral.Client;
using Cerebral.Core;
using Cerebral.Desktop.Plugins;

namespace Cerebral.Desktop;

public sealed record DesktopStartupProgress(string Stage, string Message, bool Error = false);
public sealed record DesktopStartupOutcome(bool Connected, string? Diagnostic = null)
{
    // Keep the actual local cause for the startup diagnostic surface, never IPC/JSON.
    [System.Text.Json.Serialization.JsonIgnore]
    public Exception? Error { get; init; }
}

public static class Ui
{
    private static readonly object PrimaryButtonStyleKey = new();
    private static readonly object AboutWindowTag = new();
    private static readonly DesktopThemeState ThemeState = new();
    public static readonly SolidColorBrush Ink = ThemeBrush(nameof(DesktopThemeState.Ink));
    public static readonly SolidColorBrush Muted = ThemeBrush(nameof(DesktopThemeState.Muted));
    public static readonly SolidColorBrush Accent = ThemeBrush(nameof(DesktopThemeState.Accent));
    public static readonly SolidColorBrush OnAccent = ThemeBrush(nameof(DesktopThemeState.OnAccent));
    public static readonly SolidColorBrush Line = ThemeBrush(nameof(DesktopThemeState.Line));
    public static readonly SolidColorBrush Paper = ThemeBrush(nameof(DesktopThemeState.Paper));
    public static readonly SolidColorBrush Surface = ThemeBrush(nameof(DesktopThemeState.Surface));
    public static readonly SolidColorBrush RaisedSurface = ThemeBrush(nameof(DesktopThemeState.RaisedSurface));
    public static readonly SolidColorBrush Hover = ThemeBrush(nameof(DesktopThemeState.Hover));
    public static readonly SolidColorBrush Pressed = ThemeBrush(nameof(DesktopThemeState.Pressed));
    public static readonly SolidColorBrush Selection = ThemeBrush(nameof(DesktopThemeState.Selection));
    public static readonly SolidColorBrush SelectionText = ThemeBrush(nameof(DesktopThemeState.SelectionText));
    public static readonly SolidColorBrush CodeSurface = ThemeBrush(nameof(DesktopThemeState.CodeSurface));
    public static readonly SolidColorBrush ErrorBackground = ThemeBrush(nameof(DesktopThemeState.ErrorBackground));
    public static readonly SolidColorBrush ErrorText = ThemeBrush(nameof(DesktopThemeState.ErrorText));
    public static readonly SolidColorBrush ErrorBorder = ThemeBrush(nameof(DesktopThemeState.ErrorBorder));
    private static Application? themedApplication;
    private static bool systemEventsSubscribed;
    public static string AppearanceTheme { get; private set; } = AppearanceThemePolicy.System;
    public static string ResolvedTheme { get; private set; } = AppearanceThemePolicy.Light;
    public static DesktopThemePreset AppearancePreset { get; private set; } = DesktopThemePresetPolicy.BlueWhite;

    public static void ApplyTheme(Application app)
    {
        themedApplication = app;
        TextContextMenus.Install();
        ApplyAppearanceTheme(AppearanceThemePolicy.System);
        app.Resources[SystemColors.ControlBrushKey] = Paper;
        app.Resources[SystemColors.ControlTextBrushKey] = Ink;
        app.Resources[SystemColors.WindowBrushKey] = Surface;
        app.Resources[SystemColors.WindowTextBrushKey] = Ink;
        app.Resources[SystemColors.HighlightBrushKey] = Selection;
        app.Resources[SystemColors.HighlightTextBrushKey] = SelectionText;
        app.Resources[SystemColors.InactiveSelectionHighlightBrushKey] = Selection;
        app.Resources[SystemColors.InactiveSelectionHighlightTextBrushKey] = SelectionText;
        app.Resources[SystemColors.GrayTextBrushKey] = Muted;
        app.Resources[DesktopThemeResourceKeys.TextBrush] = Ink;
        app.Resources[DesktopThemeResourceKeys.MutedTextBrush] = Muted;
        app.Resources[DesktopThemeResourceKeys.ErrorTextBrush] = ErrorText;
        app.Resources[DesktopThemeResourceKeys.ErrorBackgroundBrush] = ErrorBackground;
        app.Resources[DesktopThemeResourceKeys.ErrorBorderBrush] = ErrorBorder;
        Style style = new(typeof(Window));
        style.Setters.Add(new Setter(Control.FontFamilyProperty, new FontFamily("Segoe UI")));
        style.Setters.Add(new Setter(Control.FontSizeProperty, 14d));
        style.Setters.Add(new Setter(Control.ForegroundProperty, Ink));
        style.Setters.Add(new Setter(Control.BackgroundProperty, Paper));
        app.Resources[typeof(Window)] = style;
        // Do not pin every TextBlock to the ordinary ink color. Foreground is an inherited
        // dependency property, and pinning it here prevents text created by a control template
        // from adopting that control's selected, accent, disabled, or validation foreground.
        // The Window and owning controls provide the normal foreground; explicit Ui.Text colors
        // continue to win where a muted or status treatment is intentional.
        app.Resources[typeof(TextBlock)] = new Style(typeof(TextBlock));
        Style button = new(typeof(Button));
        button.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(13, 7, 13, 7)));
        button.Setters.Add(new Setter(FrameworkElement.MarginProperty, new Thickness(0, 0, 8, 0)));
        button.Setters.Add(new Setter(Control.BackgroundProperty, RaisedSurface));
        button.Setters.Add(new Setter(Control.BorderBrushProperty, Muted));
        button.Setters.Add(new Setter(Control.ForegroundProperty, Ink));
        button.Setters.Add(new Setter(Control.MinHeightProperty, 34d));
        button.Setters.Add(new Setter(Control.FontWeightProperty, FontWeights.SemiBold));
        button.Setters.Add(new Setter(Control.VerticalContentAlignmentProperty, VerticalAlignment.Center));
        button.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Center));
        button.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(1)));
        button.Setters.Add(new Setter(Control.TemplateProperty, BorderedContentTemplate(typeof(Button), new CornerRadius(5))));
        button.Triggers.Add(new Trigger { Property = UIElement.IsMouseOverProperty, Value = true,
            Setters = { new Setter(Control.BackgroundProperty, Hover), new Setter(Control.BorderBrushProperty, Accent) } });
        button.Triggers.Add(new Trigger { Property = System.Windows.Controls.Primitives.ButtonBase.IsPressedProperty, Value = true,
            Setters = { new Setter(Control.BackgroundProperty, Pressed) } });
        button.Triggers.Add(new Trigger { Property = UIElement.IsEnabledProperty, Value = false,
            Setters =
            {
                new Setter(Control.BackgroundProperty, Surface), new Setter(Control.ForegroundProperty, Muted),
                new Setter(Control.BorderBrushProperty, Line), new Setter(UIElement.OpacityProperty, 1d)
            } });
        app.Resources[typeof(Button)] = button;
        Style primaryButton = new(typeof(Button), button);
        primaryButton.Setters.Add(new Setter(Control.BackgroundProperty, Accent));
        primaryButton.Setters.Add(new Setter(Control.ForegroundProperty, OnAccent));
        primaryButton.Setters.Add(new Setter(Control.BorderBrushProperty, Accent));
        // The ordinary hover surface is intentionally subtle and therefore cannot retain
        // the light text used by an accent-filled primary button. Give the primary state its
        // own paired surface/text colors so moving the pointer over an action never makes its
        // label disappear in either the Light or Dark palette.
        primaryButton.Triggers.Add(new Trigger { Property = UIElement.IsMouseOverProperty, Value = true,
            Setters = { new Setter(Control.BackgroundProperty, Selection), new Setter(Control.ForegroundProperty, SelectionText),
                new Setter(Control.BorderBrushProperty, Accent) } });
        primaryButton.Triggers.Add(new Trigger { Property = System.Windows.Controls.Primitives.ButtonBase.IsPressedProperty, Value = true,
            Setters = { new Setter(Control.BackgroundProperty, Pressed), new Setter(Control.ForegroundProperty, Ink),
                new Setter(Control.BorderBrushProperty, Accent) } });
        app.Resources[PrimaryButtonStyleKey] = primaryButton;
        foreach (var type in new[] { typeof(TextBox), typeof(PasswordBox) })
        {
            Style field = new(type);
            field.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(7)));
            field.Setters.Add(new Setter(Control.BorderBrushProperty, Line));
            field.Setters.Add(new Setter(Control.BackgroundProperty, Surface));
            field.Setters.Add(new Setter(Control.ForegroundProperty, Ink));
            field.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(1)));
            field.Setters.Add(new Setter(Control.TemplateProperty, TextFieldTemplate(type)));
            if (type == typeof(TextBox))
            {
                field.Setters.Add(new Setter(System.Windows.Controls.Primitives.TextBoxBase.SelectionBrushProperty, Selection));
                field.Setters.Add(new Setter(System.Windows.Controls.Primitives.TextBoxBase.SelectionTextBrushProperty, SelectionText));
                field.Setters.Add(new Setter(System.Windows.Controls.Primitives.TextBoxBase.SelectionOpacityProperty, 1d));
                field.Setters.Add(new Setter(TextBox.CaretBrushProperty, Ink));
            }
            field.Triggers.Add(new Trigger { Property = UIElement.IsKeyboardFocusWithinProperty, Value = true,
                Setters = { new Setter(Control.BorderBrushProperty, Accent) } });
            field.Triggers.Add(new Trigger { Property = UIElement.IsEnabledProperty, Value = false,
                Setters = { new Setter(Control.BackgroundProperty, Surface), new Setter(Control.ForegroundProperty, Muted),
                    new Setter(Control.BorderBrushProperty, Line), new Setter(UIElement.OpacityProperty, 1d) } });
            app.Resources[type] = field;
        }
        // Own both the selection surface and its ink: the Windows template can keep a
        // light gradient even after a dark theme supplies the selected-value foreground.
        Style combo = new(typeof(ComboBox));
        combo.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(7)));
        combo.Setters.Add(new Setter(Control.BorderBrushProperty, Muted));
        combo.Setters.Add(new Setter(Control.BackgroundProperty, Surface));
        combo.Setters.Add(new Setter(Control.ForegroundProperty, Ink));
        combo.Setters.Add(new Setter(Control.TemplateProperty, ComboBoxTemplate()));
        combo.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(1)));
        combo.Setters.Add(new Setter(Control.MinHeightProperty, 34d));
        combo.Triggers.Add(new Trigger { Property = UIElement.IsKeyboardFocusWithinProperty, Value = true,
            Setters = { new Setter(Control.BorderBrushProperty, Accent) } });
        combo.Triggers.Add(new Trigger { Property = UIElement.IsEnabledProperty, Value = false,
            Setters = { new Setter(Control.BackgroundProperty, Surface), new Setter(Control.ForegroundProperty, Muted),
                new Setter(Control.BorderBrushProperty, Line), new Setter(UIElement.OpacityProperty, 1d) } });
        app.Resources[typeof(ComboBox)] = combo;
        AddCheckBoxStyle(app);
        Style grid = new(typeof(DataGrid));
        grid.Setters.Add(new Setter(DataGrid.AutoGenerateColumnsProperty, false));
        grid.Setters.Add(new Setter(DataGrid.IsReadOnlyProperty, true));
        grid.Setters.Add(new Setter(DataGrid.CanUserAddRowsProperty, false));
        grid.Setters.Add(new Setter(DataGrid.CanUserDeleteRowsProperty, false));
        grid.Setters.Add(new Setter(DataGrid.RowHeaderWidthProperty, 0d));
        grid.Setters.Add(new Setter(DataGrid.GridLinesVisibilityProperty, DataGridGridLinesVisibility.None));
        grid.Setters.Add(new Setter(DataGrid.HorizontalGridLinesBrushProperty, Line));
        grid.Setters.Add(new Setter(DataGrid.SelectionModeProperty, DataGridSelectionMode.Single));
        grid.Setters.Add(new Setter(Control.BorderBrushProperty, Line));
        grid.Setters.Add(new Setter(DataGrid.AlternatingRowBackgroundProperty, Paper));
        grid.Setters.Add(new Setter(DataGrid.MinRowHeightProperty, 34d));
        grid.Setters.Add(new Setter(DataGrid.ColumnHeaderHeightProperty, 38d));
        grid.Setters.Add(new Setter(Control.BackgroundProperty, Surface));
        grid.Setters.Add(new Setter(Control.ForegroundProperty, Ink));
        app.Resources[typeof(DataGrid)] = grid;
        Style columnHeader = new(typeof(System.Windows.Controls.Primitives.DataGridColumnHeader));
        columnHeader.Setters.Add(new Setter(Control.BackgroundProperty, Paper));
        columnHeader.Setters.Add(new Setter(Control.ForegroundProperty, Muted));
        columnHeader.Setters.Add(new Setter(Control.FontWeightProperty, FontWeights.SemiBold));
        columnHeader.Setters.Add(new Setter(Control.FontSizeProperty, 12d));
        columnHeader.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(10, 7, 10, 7)));
        columnHeader.Setters.Add(new Setter(Control.BorderBrushProperty, Line));
        columnHeader.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(0, 0, 0, 1)));
        app.Resources[typeof(System.Windows.Controls.Primitives.DataGridColumnHeader)] = columnHeader;
        Style cell = new(typeof(DataGridCell));
        cell.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(9, 5, 9, 5)));
        cell.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(0)));
        cell.Setters.Add(new Setter(Control.VerticalContentAlignmentProperty, VerticalAlignment.Center));
        app.Resources[typeof(DataGridCell)] = cell;

        Style row = new(typeof(DataGridRow));
        row.Setters.Add(new Setter(Control.ForegroundProperty, Ink));
        row.Setters.Add(new Setter(Control.BackgroundProperty, Surface));
        row.Triggers.Add(new Trigger { Property = DataGridRow.IsSelectedProperty, Value = true,
            Setters = { new Setter(Control.BackgroundProperty, Selection), new Setter(Control.ForegroundProperty, SelectionText) } });
        var rowHover = new MultiTrigger();
        rowHover.Conditions.Add(new Condition(UIElement.IsMouseOverProperty, true));
        rowHover.Conditions.Add(new Condition(DataGridRow.IsSelectedProperty, false));
        rowHover.Setters.Add(new Setter(Control.BackgroundProperty, Hover));
        row.Triggers.Add(rowHover);
        app.Resources[typeof(DataGridRow)] = row;

        // List controls need their own surface as well as item ink. The Windows list
        // theme can retain a white field even when its child items adopt dark-mode ink.
        AddListStyle<ListBox>(app);
        AddListStyle<ListView>(app);
        AddListStyle<TreeView>(app);
        AddSelectionStyle<ListBoxItem>(app, BorderedContentTemplate(typeof(ListBoxItem), new CornerRadius(5)));
        AddSelectionStyle<ListViewItem>(app, BorderedContentTemplate(typeof(ListViewItem), new CornerRadius(5), supportsGridView: true));
        AddSelectionStyle<TreeViewItem>(app);
        AddComboBoxItemStyle(app);
        AddTabItemStyle(app);
        var tabs = new Style(typeof(TabControl));
        tabs.Setters.Add(new Setter(Control.BackgroundProperty, Surface));
        tabs.Setters.Add(new Setter(Control.ForegroundProperty, Ink));
        tabs.Setters.Add(new Setter(Control.BorderBrushProperty, Line));
        app.Resources[typeof(TabControl)] = tabs;

        if (!systemEventsSubscribed)
        {
            SystemEvents.UserPreferenceChanged += SystemPreferenceChanged;
            app.Exit += (_, _) =>
            {
                SystemEvents.UserPreferenceChanged -= SystemPreferenceChanged;
                systemEventsSubscribed = false;
                themedApplication = null;
            };
            systemEventsSubscribed = true;
        }
    }

    public static void ApplyAppearanceTheme(string? preference, Func<bool>? systemUsesLight = null)
    {
        AppearanceTheme = AppearanceThemePolicy.Effective(preference);
        ApplyThemeDefinition(DesktopThemeCatalog.Resolve(AppearancePreset, AppearanceTheme, systemUsesLight));
    }

    /// <summary>Validated data-only preset; brush instances remain stable across all existing views.</summary>
    public static void ApplyThemePreset(DesktopThemePreset preset)
    {
        using var document = JsonDocument.Parse(DesktopThemePresetPolicy.Serialize(preset));
        AppearancePreset = DesktopThemePresetPolicy.Parse(document.RootElement);
        ApplyAppearanceTheme(AppearanceTheme);
    }

    /// <summary>Applies a complete named definition through the shared rendering boundary.</summary>
    public static void ApplyThemeDefinition(DesktopThemeDefinition theme)
    {
        ArgumentNullException.ThrowIfNull(theme);
        ResolvedTheme = theme.Id;
        ThemeState.Apply(theme);
    }

    private static SolidColorBrush ThemeBrush(string property)
    {
        var brush = new SolidColorBrush();
        BindingOperations.SetBinding(brush, SolidColorBrush.ColorProperty,
            new Binding(property) { Source = ThemeState, Mode = BindingMode.OneWay });
        return brush;
    }

    private sealed class DesktopThemeState : INotifyPropertyChanged
    {
        private Color ink, muted, accent, onAccent, line, paper, surface, raisedSurface,
            hover, pressed, selection, selectionText, codeSurface, errorBackground, errorText, errorBorder;
        public Color Ink { get => ink; private set => Set(ref ink, value, nameof(Ink)); }
        public Color Muted { get => muted; private set => Set(ref muted, value, nameof(Muted)); }
        public Color Accent { get => accent; private set => Set(ref accent, value, nameof(Accent)); }
        public Color OnAccent { get => onAccent; private set => Set(ref onAccent, value, nameof(OnAccent)); }
        public Color Line { get => line; private set => Set(ref line, value, nameof(Line)); }
        public Color Paper { get => paper; private set => Set(ref paper, value, nameof(Paper)); }
        public Color Surface { get => surface; private set => Set(ref surface, value, nameof(Surface)); }
        public Color RaisedSurface { get => raisedSurface; private set => Set(ref raisedSurface, value, nameof(RaisedSurface)); }
        public Color Hover { get => hover; private set => Set(ref hover, value, nameof(Hover)); }
        public Color Pressed { get => pressed; private set => Set(ref pressed, value, nameof(Pressed)); }
        public Color Selection { get => selection; private set => Set(ref selection, value, nameof(Selection)); }
        public Color SelectionText { get => selectionText; private set => Set(ref selectionText, value, nameof(SelectionText)); }
        public Color CodeSurface { get => codeSurface; private set => Set(ref codeSurface, value, nameof(CodeSurface)); }
        public Color ErrorBackground { get => errorBackground; private set => Set(ref errorBackground, value, nameof(ErrorBackground)); }
        public Color ErrorText { get => errorText; private set => Set(ref errorText, value, nameof(ErrorText)); }
        public Color ErrorBorder { get => errorBorder; private set => Set(ref errorBorder, value, nameof(ErrorBorder)); }
        public event PropertyChangedEventHandler? PropertyChanged;

        public void Apply(DesktopThemeDefinition theme)
        {
            Ink = theme.Ink; Muted = theme.Muted; Accent = theme.Accent; OnAccent = theme.OnAccent;
            Line = theme.Line; Paper = theme.Window; Surface = theme.Surface; RaisedSurface = theme.RaisedSurface;
            Hover = theme.Hover; Pressed = theme.Pressed; Selection = theme.Selection;
            SelectionText = theme.SelectionText; CodeSurface = theme.CodeSurface;
            ErrorBackground = theme.ErrorBackground; ErrorText = theme.ErrorText; ErrorBorder = theme.ErrorBorder;
        }

        private void Set(ref Color field, Color value, string property)
        {
            if (field == value) return;
            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
        }
    }

    private static void AddListStyle<T>(Application app) where T : Control
    {
        var style = new Style(typeof(T));
        style.Setters.Add(new Setter(Control.BackgroundProperty, Surface));
        style.Setters.Add(new Setter(Control.ForegroundProperty, Ink));
        style.Setters.Add(new Setter(Control.BorderBrushProperty, Line));
        style.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(1)));
        style.Triggers.Add(new Trigger { Property = UIElement.IsKeyboardFocusWithinProperty, Value = true,
            Setters = { new Setter(Control.BorderBrushProperty, Accent) } });
        style.Triggers.Add(new Trigger { Property = UIElement.IsEnabledProperty, Value = false,
            Setters = { new Setter(Control.ForegroundProperty, Muted), new Setter(Control.BorderBrushProperty, Line),
                new Setter(UIElement.OpacityProperty, 1d) } });
        app.Resources[typeof(T)] = style;
    }

    private static void AddSelectionStyle<T>(Application app, ControlTemplate? template = null) where T : Control
    {
        var style = new Style(typeof(T));
        style.Setters.Add(new Setter(Control.ForegroundProperty, Ink));
        style.Setters.Add(new Setter(Control.BackgroundProperty, Brushes.Transparent));
        if (template is not null)
        {
            style.Setters.Add(new Setter(Control.TemplateProperty, template));
            style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(7, 5, 7, 5)));
            style.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch));
            style.Setters.Add(new Setter(Control.VerticalContentAlignmentProperty, VerticalAlignment.Center));
        }
        style.Triggers.Add(new Trigger { Property = System.Windows.Controls.Primitives.Selector.IsSelectedProperty, Value = true,
            Setters = { new Setter(Control.BackgroundProperty, Selection), new Setter(Control.ForegroundProperty, SelectionText) } });
        var hover = new MultiTrigger();
        hover.Conditions.Add(new Condition(UIElement.IsMouseOverProperty, true));
        hover.Conditions.Add(new Condition(System.Windows.Controls.Primitives.Selector.IsSelectedProperty, false));
        hover.Setters.Add(new Setter(Control.BackgroundProperty, Hover));
        hover.Setters.Add(new Setter(Control.ForegroundProperty, Ink));
        style.Triggers.Add(hover);
        style.Triggers.Add(new Trigger { Property = UIElement.IsEnabledProperty, Value = false,
            Setters = { new Setter(Control.BackgroundProperty, Surface), new Setter(Control.ForegroundProperty, Muted), new Setter(UIElement.OpacityProperty, 1d) } });
        app.Resources[typeof(T)] = style;
    }

    private static void AddCheckBoxStyle(Application app)
    {
        var style = new Style(typeof(CheckBox));
        style.Setters.Add(new Setter(Control.ForegroundProperty, Ink));
        style.Setters.Add(new Setter(Control.BackgroundProperty, Surface));
        style.Setters.Add(new Setter(Control.BorderBrushProperty, Brushes.Transparent));
        style.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(1)));
        style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(7, 5, 7, 5)));
        style.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch));
        style.Setters.Add(new Setter(Control.VerticalContentAlignmentProperty, VerticalAlignment.Center));
        style.Setters.Add(new Setter(Control.MinHeightProperty, 32d));
        style.Setters.Add(new Setter(Control.TemplateProperty, CheckBoxTemplate()));
        style.Triggers.Add(new Trigger { Property = UIElement.IsMouseOverProperty, Value = true,
            Setters = { new Setter(Control.BackgroundProperty, Hover), new Setter(Control.ForegroundProperty, Ink),
                new Setter(Control.BorderBrushProperty, Accent) } });
        style.Triggers.Add(new Trigger { Property = System.Windows.Controls.Primitives.ButtonBase.IsPressedProperty, Value = true,
            Setters = { new Setter(Control.BackgroundProperty, Pressed), new Setter(Control.ForegroundProperty, Ink),
                new Setter(Control.BorderBrushProperty, Accent) } });
        style.Triggers.Add(new Trigger { Property = UIElement.IsKeyboardFocusWithinProperty, Value = true,
            Setters = { new Setter(Control.BorderBrushProperty, Accent) } });
        style.Triggers.Add(new Trigger { Property = UIElement.IsEnabledProperty, Value = false,
            Setters = { new Setter(Control.BackgroundProperty, Surface), new Setter(Control.ForegroundProperty, Muted),
                new Setter(Control.BorderBrushProperty, Line), new Setter(UIElement.OpacityProperty, 1d) } });
        app.Resources[typeof(CheckBox)] = style;
    }

    private static void AddTabItemStyle(Application app)
    {
        var style = new Style(typeof(TabItem));
        style.Setters.Add(new Setter(Control.ForegroundProperty, Ink));
        style.Setters.Add(new Setter(Control.BackgroundProperty, Surface));
        style.Setters.Add(new Setter(Control.BorderBrushProperty, Line));
        style.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(1)));
        style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(12, 8, 12, 8)));
        // TabControl also uses the selected TabItem's content alignment for the page host.
        // Keep pages stretched from the top; the custom header presenter centers only the label.
        style.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch));
        style.Setters.Add(new Setter(Control.VerticalContentAlignmentProperty, VerticalAlignment.Stretch));
        style.Setters.Add(new Setter(Control.TemplateProperty, BorderedHeaderTemplate()));
        style.Triggers.Add(new Trigger { Property = System.Windows.Controls.Primitives.Selector.IsSelectedProperty, Value = true,
            Setters = { new Setter(Control.BackgroundProperty, Selection), new Setter(Control.ForegroundProperty, SelectionText), new Setter(Control.BorderBrushProperty, Selection) } });
        // A selected TabItem owns the active page, so IsMouseOver can remain true while the
        // pointer moves anywhere through that page. Restrict hover visuals to inactive tabs;
        // otherwise the selected header oscillates between Selection and Hover as descendants
        // gain and lose hit testing during ordinary pointer movement.
        var hover = new MultiTrigger();
        hover.Conditions.Add(new Condition(UIElement.IsMouseOverProperty, true));
        hover.Conditions.Add(new Condition(System.Windows.Controls.Primitives.Selector.IsSelectedProperty, false));
        hover.Setters.Add(new Setter(Control.BackgroundProperty, Hover));
        hover.Setters.Add(new Setter(Control.ForegroundProperty, Ink));
        hover.Setters.Add(new Setter(Control.BorderBrushProperty, Accent));
        style.Triggers.Add(hover);
        style.Triggers.Add(new Trigger { Property = UIElement.IsEnabledProperty, Value = false,
            Setters = { new Setter(Control.BackgroundProperty, Surface), new Setter(Control.ForegroundProperty, Muted), new Setter(Control.BorderBrushProperty, Line), new Setter(UIElement.OpacityProperty, 1d) } });
        app.Resources[typeof(TabItem)] = style;
    }

    private static ControlTemplate BorderedContentTemplate(Type controlType, CornerRadius radius, bool supportsGridView = false)
    {
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
        border.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(Control.BorderBrushProperty));
        border.SetValue(Border.BorderThicknessProperty, new TemplateBindingExtension(Control.BorderThicknessProperty));
        border.SetValue(Border.PaddingProperty, new TemplateBindingExtension(Control.PaddingProperty));
        border.SetValue(Border.CornerRadiusProperty, radius);
        var content = new FrameworkElementFactory(typeof(ContentPresenter), "ContentPresenter");
        content.SetValue(ContentPresenter.ContentProperty, new TemplateBindingExtension(ContentControl.ContentProperty));
        content.SetValue(ContentPresenter.ContentTemplateProperty, new TemplateBindingExtension(ContentControl.ContentTemplateProperty));
        // DisplayMemberPath and caller selectors reach an item through ContentTemplateSelector.
        content.SetValue(ContentPresenter.ContentTemplateSelectorProperty, new TemplateBindingExtension(ContentControl.ContentTemplateSelectorProperty));
        content.SetValue(ContentPresenter.ContentStringFormatProperty, new TemplateBindingExtension(ContentControl.ContentStringFormatProperty));
        content.SetValue(ContentPresenter.HorizontalAlignmentProperty, new TemplateBindingExtension(Control.HorizontalContentAlignmentProperty));
        content.SetValue(ContentPresenter.VerticalAlignmentProperty, new TemplateBindingExtension(Control.VerticalContentAlignmentProperty));
        content.SetValue(ContentPresenter.RecognizesAccessKeyProperty, true);
        if (!supportsGridView)
        {
            border.AppendChild(content);
            return new ControlTemplate(controlType) { VisualTree = border };
        }
        // A ListView can use ordinary item content or GridView columns. Own the item
        // chrome without discarding the GridView row presenter or its live columns.
        var grid = new FrameworkElementFactory(typeof(Grid));
        content.SetValue(UIElement.VisibilityProperty, Visibility.Collapsed);
        grid.AppendChild(content);
        var row = new FrameworkElementFactory(typeof(GridViewRowPresenter), "GridViewRowPresenter");
        row.SetValue(GridViewRowPresenter.ContentProperty, new TemplateBindingExtension(ContentControl.ContentProperty));
        row.SetBinding(GridViewRowPresenter.ColumnsProperty, new Binding("View.Columns")
            { RelativeSource = new(RelativeSourceMode.FindAncestor, typeof(ListView), 1) });
        grid.AppendChild(row);
        border.AppendChild(grid);
        var template = new ControlTemplate(controlType) { VisualTree = border };
        template.Triggers.Add(new DataTrigger
        {
            Binding = new Binding("View.Columns")
                { RelativeSource = new(RelativeSourceMode.FindAncestor, typeof(ListView), 1), FallbackValue = null },
            Value = null,
            Setters = { new Setter(UIElement.VisibilityProperty, Visibility.Visible, "ContentPresenter"),
                new Setter(UIElement.VisibilityProperty, Visibility.Collapsed, "GridViewRowPresenter") }
        });
        return template;
    }

    private static ControlTemplate BorderedHeaderTemplate()
    {
        // A TabItem is a HeaderedContentControl: its Content is the page mounted by the
        // owning TabControl, while its Header belongs in the strip. Reusing the ordinary
        // ContentControl template here reparents the page into the strip and can leave the
        // entire selected surface blank.
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
        border.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(Control.BorderBrushProperty));
        border.SetValue(Border.BorderThicknessProperty, new TemplateBindingExtension(Control.BorderThicknessProperty));
        border.SetValue(Border.PaddingProperty, new TemplateBindingExtension(Control.PaddingProperty));
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(5, 5, 0, 0));
        var header = new FrameworkElementFactory(typeof(ContentPresenter));
        header.SetValue(ContentPresenter.ContentProperty, new TemplateBindingExtension(HeaderedContentControl.HeaderProperty));
        header.SetValue(ContentPresenter.ContentTemplateProperty, new TemplateBindingExtension(HeaderedContentControl.HeaderTemplateProperty));
        header.SetValue(ContentPresenter.ContentStringFormatProperty, new TemplateBindingExtension(HeaderedContentControl.HeaderStringFormatProperty));
        header.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        header.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
        header.SetValue(ContentPresenter.RecognizesAccessKeyProperty, true);
        border.AppendChild(header);
        return new ControlTemplate(typeof(TabItem)) { VisualTree = border };
    }

    private static ControlTemplate TextFieldTemplate(Type controlType)
    {
        // Preserve the WPF editor's required content host and native editing semantics, while
        // owning only chrome; the platform template otherwise dims disabled text internally.
        var border = new FrameworkElementFactory(typeof(Border), "TextFieldBorder");
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(5));
        border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
        border.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(Control.BorderBrushProperty));
        border.SetValue(Border.BorderThicknessProperty, new TemplateBindingExtension(Control.BorderThicknessProperty));
        var content = new FrameworkElementFactory(typeof(ScrollViewer), "PART_ContentHost");
        content.SetValue(FrameworkElement.MarginProperty, new TemplateBindingExtension(Control.PaddingProperty));
        content.SetValue(UIElement.FocusableProperty, false);
        if (controlType == typeof(TextBox))
        {
            var parent = new RelativeSource(RelativeSourceMode.TemplatedParent);
            content.SetBinding(ScrollViewer.HorizontalScrollBarVisibilityProperty,
                new Binding(nameof(TextBox.HorizontalScrollBarVisibility)) { RelativeSource = parent });
            content.SetBinding(ScrollViewer.VerticalScrollBarVisibilityProperty,
                new Binding(nameof(TextBox.VerticalScrollBarVisibility)) { RelativeSource = parent });
        }
        border.AppendChild(content);
        return new ControlTemplate(controlType) { VisualTree = border };
    }

    private static ControlTemplate CheckBoxTemplate()
    {
        var chrome = new FrameworkElementFactory(typeof(Border), "CheckBoxChrome");
        chrome.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
        chrome.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(Control.BorderBrushProperty));
        chrome.SetValue(Border.BorderThicknessProperty, new TemplateBindingExtension(Control.BorderThicknessProperty));
        chrome.SetValue(Border.PaddingProperty, new TemplateBindingExtension(Control.PaddingProperty));
        chrome.SetValue(Border.CornerRadiusProperty, new CornerRadius(5));
        chrome.SetValue(UIElement.SnapsToDevicePixelsProperty, true);

        var row = new FrameworkElementFactory(typeof(DockPanel));
        row.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Stretch);
        var indicator = new FrameworkElementFactory(typeof(Border), "CheckBoxIndicator");
        indicator.SetValue(DockPanel.DockProperty, Dock.Left);
        indicator.SetValue(FrameworkElement.WidthProperty, 18d);
        indicator.SetValue(FrameworkElement.HeightProperty, 18d);
        indicator.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 0, 8, 0));
        indicator.SetValue(Border.BackgroundProperty, Surface);
        indicator.SetValue(Border.BorderBrushProperty, Muted);
        indicator.SetValue(Border.BorderThicknessProperty, new Thickness(1));
        indicator.SetValue(Border.CornerRadiusProperty, new CornerRadius(5));

        var glyphs = new FrameworkElementFactory(typeof(Grid));
        var check = new FrameworkElementFactory(typeof(System.Windows.Shapes.Path), "CheckBoxCheckMark");
        check.SetValue(System.Windows.Shapes.Path.DataProperty, Geometry.Parse("M 3,9 L 7,13 L 15,4"));
        check.SetValue(System.Windows.Shapes.Shape.StrokeProperty, OnAccent);
        check.SetValue(System.Windows.Shapes.Shape.StrokeThicknessProperty, 2d);
        check.SetValue(System.Windows.Shapes.Shape.StrokeStartLineCapProperty, PenLineCap.Round);
        check.SetValue(System.Windows.Shapes.Shape.StrokeEndLineCapProperty, PenLineCap.Round);
        check.SetValue(System.Windows.Shapes.Shape.StrokeLineJoinProperty, PenLineJoin.Round);
        check.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        check.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        check.SetValue(UIElement.VisibilityProperty, Visibility.Collapsed);
        glyphs.AppendChild(check);
        var indeterminate = new FrameworkElementFactory(typeof(System.Windows.Shapes.Rectangle), "CheckBoxIndeterminateMark");
        indeterminate.SetValue(FrameworkElement.WidthProperty, 10d);
        indeterminate.SetValue(FrameworkElement.HeightProperty, 2d);
        indeterminate.SetValue(System.Windows.Shapes.Shape.FillProperty, SelectionText);
        indeterminate.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        indeterminate.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        indeterminate.SetValue(UIElement.VisibilityProperty, Visibility.Collapsed);
        glyphs.AppendChild(indeterminate);
        indicator.AppendChild(glyphs);
        row.AppendChild(indicator);

        var content = new FrameworkElementFactory(typeof(ContentPresenter), "CheckBoxContent");
        content.SetValue(ContentPresenter.ContentProperty, new TemplateBindingExtension(ContentControl.ContentProperty));
        content.SetValue(ContentPresenter.ContentTemplateProperty, new TemplateBindingExtension(ContentControl.ContentTemplateProperty));
        content.SetValue(ContentPresenter.ContentTemplateSelectorProperty, new TemplateBindingExtension(ContentControl.ContentTemplateSelectorProperty));
        content.SetValue(ContentPresenter.ContentStringFormatProperty, new TemplateBindingExtension(ContentControl.ContentStringFormatProperty));
        content.SetValue(ContentPresenter.HorizontalAlignmentProperty, new TemplateBindingExtension(Control.HorizontalContentAlignmentProperty));
        content.SetValue(ContentPresenter.VerticalAlignmentProperty, new TemplateBindingExtension(Control.VerticalContentAlignmentProperty));
        content.SetValue(ContentPresenter.RecognizesAccessKeyProperty, true);
        content.SetValue(System.Windows.Documents.TextElement.ForegroundProperty, new TemplateBindingExtension(Control.ForegroundProperty));
        row.AppendChild(content);
        chrome.AppendChild(row);

        var template = new ControlTemplate(typeof(CheckBox)) { VisualTree = chrome };
        template.Triggers.Add(new Trigger
        {
            Property = System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty,
            Value = true,
            Setters =
            {
                new Setter(Border.BackgroundProperty, Accent, "CheckBoxIndicator"),
                new Setter(Border.BorderBrushProperty, Accent, "CheckBoxIndicator"),
                new Setter(UIElement.VisibilityProperty, Visibility.Visible, "CheckBoxCheckMark")
            }
        });
        template.Triggers.Add(new Trigger
        {
            Property = System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty,
            Value = null,
            Setters =
            {
                new Setter(Border.BackgroundProperty, Selection, "CheckBoxIndicator"),
                new Setter(Border.BorderBrushProperty, Selection, "CheckBoxIndicator"),
                new Setter(UIElement.VisibilityProperty, Visibility.Visible, "CheckBoxIndeterminateMark")
            }
        });
        template.Triggers.Add(new Trigger
        {
            Property = UIElement.IsEnabledProperty,
            Value = false,
            Setters =
            {
                new Setter(Border.BackgroundProperty, Surface, "CheckBoxIndicator"),
                new Setter(Border.BorderBrushProperty, Muted, "CheckBoxIndicator"),
                new Setter(System.Windows.Shapes.Shape.StrokeProperty, Muted, "CheckBoxCheckMark"),
                new Setter(System.Windows.Shapes.Shape.FillProperty, Muted, "CheckBoxIndeterminateMark")
            }
        });
        return template;
    }

    public static TabItem TabPage(string title, UIElement content, Thickness? padding = null)
    {
        var tab = new TabItem { Header = title };
        if (padding is { } inset) tab.Padding = inset;
        SetTabPageContent(tab, content);
        return tab;
    }

    public static void SetTabPageContent(TabItem tab, UIElement content)
    {
        // WPF inherits through logical parents: selected header ink otherwise reaches
        // the page even when a separate presenter renders it. Set one live boundary at
        // the page root, preserving any explicit color supplied by the page or plugin.
        if (content is FrameworkElement page &&
            DependencyPropertyHelper.GetValueSource(page, System.Windows.Documents.TextElement.ForegroundProperty).BaseValueSource
                is BaseValueSource.Default or BaseValueSource.Inherited)
            page.SetResourceReference(System.Windows.Documents.TextElement.ForegroundProperty, DesktopThemeResourceKeys.TextBrush);
        tab.Content = content;
    }

    private static void AddComboBoxItemStyle(Application app)
    {
        var style = new Style(typeof(ComboBoxItem));
        style.Setters.Add(new Setter(Control.ForegroundProperty, Ink));
        style.Setters.Add(new Setter(Control.BackgroundProperty, Surface));
        style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(8, 6, 8, 6)));
        style.Triggers.Add(new Trigger { Property = System.Windows.Controls.Primitives.Selector.IsSelectedProperty, Value = true,
            Setters = { new Setter(Control.BackgroundProperty, Accent), new Setter(Control.ForegroundProperty, OnAccent) } });
        style.Triggers.Add(new Trigger { Property = UIElement.IsMouseOverProperty, Value = true,
            Setters = { new Setter(Control.BackgroundProperty, Accent), new Setter(Control.ForegroundProperty, OnAccent) } });
        app.Resources[typeof(ComboBoxItem)] = style;
    }

    private static ControlTemplate ComboBoxTemplate()
    {
        static Binding Parent(string property, BindingMode mode = BindingMode.OneWay) =>
            new(property) { RelativeSource = new(RelativeSourceMode.TemplatedParent), Mode = mode };
        var root = new FrameworkElementFactory(typeof(Grid));
        var toggle = new FrameworkElementFactory(typeof(System.Windows.Controls.Primitives.ToggleButton), "DropDownToggle");
        toggle.SetValue(Control.FocusableProperty, false);
        toggle.SetValue(System.Windows.Controls.Primitives.ToggleButton.ClickModeProperty, ClickMode.Press);
        toggle.SetBinding(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty, Parent(nameof(ComboBox.IsDropDownOpen), BindingMode.TwoWay));
        toggle.SetBinding(Control.BackgroundProperty, Parent(nameof(Control.Background)));
        toggle.SetBinding(Control.BorderBrushProperty, Parent(nameof(Control.BorderBrush)));
        toggle.SetBinding(Control.BorderThicknessProperty, Parent(nameof(Control.BorderThickness)));
        toggle.SetBinding(Control.ForegroundProperty, Parent(nameof(Control.Foreground)));
        var frame = new FrameworkElementFactory(typeof(Border));
        frame.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
        frame.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(Control.BorderBrushProperty));
        frame.SetValue(Border.BorderThicknessProperty, new TemplateBindingExtension(Control.BorderThicknessProperty));
        frame.SetValue(Border.CornerRadiusProperty, new CornerRadius(5));
        var arrow = new FrameworkElementFactory(typeof(TextBlock));
        arrow.SetValue(TextBlock.TextProperty, "▾");
        arrow.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Right);
        arrow.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        arrow.SetValue(FrameworkElement.MarginProperty, new Thickness(10, 0, 10, 0));
        arrow.SetValue(TextBlock.ForegroundProperty, new TemplateBindingExtension(Control.ForegroundProperty));
        frame.AppendChild(arrow);
        toggle.SetValue(Control.TemplateProperty, new ControlTemplate(typeof(System.Windows.Controls.Primitives.ToggleButton)) { VisualTree = frame });
        root.AppendChild(toggle);
        var selection = new FrameworkElementFactory(typeof(ContentPresenter), "SelectionPresenter");
        selection.SetBinding(ContentPresenter.ContentProperty, Parent(nameof(ComboBox.SelectionBoxItem)));
        selection.SetBinding(ContentPresenter.ContentTemplateProperty, Parent(nameof(ComboBox.SelectionBoxItemTemplate)));
        selection.SetBinding(ContentPresenter.ContentTemplateSelectorProperty, Parent(nameof(ComboBox.ItemTemplateSelector)));
        selection.SetBinding(ContentPresenter.ContentStringFormatProperty, Parent(nameof(ComboBox.SelectionBoxItemStringFormat)));
        selection.SetBinding(System.Windows.Documents.TextElement.ForegroundProperty, Parent(nameof(Control.Foreground)));
        selection.SetValue(FrameworkElement.MarginProperty, new Thickness(8, 5, 30, 5));
        selection.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        selection.SetValue(UIElement.IsHitTestVisibleProperty, false);
        root.AppendChild(selection);
        var editor = new FrameworkElementFactory(typeof(TextBox), "PART_EditableTextBox");
        editor.SetValue(UIElement.VisibilityProperty, Visibility.Collapsed);
        editor.SetValue(FrameworkElement.MarginProperty, new Thickness(3, 2, 30, 2));
        editor.SetValue(Control.BorderThicknessProperty, new Thickness(0));
        editor.SetBinding(Control.BackgroundProperty, Parent(nameof(Control.Background)));
        editor.SetBinding(Control.ForegroundProperty, Parent(nameof(Control.Foreground)));
        editor.SetBinding(TextBox.IsReadOnlyProperty, Parent(nameof(ComboBox.IsReadOnly)));
        root.AppendChild(editor);
        var popup = new FrameworkElementFactory(typeof(System.Windows.Controls.Primitives.Popup), "PART_Popup");
        popup.SetValue(System.Windows.Controls.Primitives.Popup.PlacementProperty, System.Windows.Controls.Primitives.PlacementMode.Bottom);
        popup.SetValue(System.Windows.Controls.Primitives.Popup.AllowsTransparencyProperty, true);
        popup.SetBinding(System.Windows.Controls.Primitives.Popup.IsOpenProperty, Parent(nameof(ComboBox.IsDropDownOpen)));
        var dropdown = new FrameworkElementFactory(typeof(Border));
        dropdown.SetValue(Border.BackgroundProperty, Surface);
        dropdown.SetValue(Border.BorderBrushProperty, Line);
        dropdown.SetValue(Border.BorderThicknessProperty, new Thickness(1));
        dropdown.SetValue(Border.CornerRadiusProperty, new CornerRadius(5));
        dropdown.SetValue(Border.PaddingProperty, new Thickness(3));
        dropdown.SetBinding(FrameworkElement.MinWidthProperty, Parent(nameof(FrameworkElement.ActualWidth)));
        var scroll = new FrameworkElementFactory(typeof(ScrollViewer));
        scroll.SetValue(ScrollViewer.VerticalScrollBarVisibilityProperty, ScrollBarVisibility.Auto);
        scroll.SetValue(ScrollViewer.HorizontalScrollBarVisibilityProperty, ScrollBarVisibility.Disabled);
        scroll.SetValue(ScrollViewer.CanContentScrollProperty, true);
        scroll.SetBinding(FrameworkElement.MaxHeightProperty, Parent(nameof(ComboBox.MaxDropDownHeight)));
        scroll.AppendChild(new FrameworkElementFactory(typeof(ItemsPresenter)));
        dropdown.AppendChild(scroll); popup.AppendChild(dropdown); root.AppendChild(popup);
        var template = new ControlTemplate(typeof(ComboBox)) { VisualTree = root };
        template.Triggers.Add(new Trigger { Property = ComboBox.IsEditableProperty, Value = true,
            Setters = { new Setter(UIElement.VisibilityProperty, Visibility.Collapsed, "SelectionPresenter"),
                new Setter(UIElement.VisibilityProperty, Visibility.Visible, "PART_EditableTextBox") } });
        return template;
    }

    private static void SystemPreferenceChanged(object sender, UserPreferenceChangedEventArgs args)
    {
        var app = themedApplication;
        if (app is null || AppearanceTheme != AppearanceThemePolicy.System) return;
        _ = app.Dispatcher.BeginInvoke(new Action(() => ApplyAppearanceTheme(AppearanceThemePolicy.System)));
    }

    public static TextBlock Text(string text, double size = 14, Brush? color = null)
    {
        var block = new TextBlock
        {
            Text = text, FontSize = size, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8)
        };
        // Ordinary text follows its owning control's live foreground. A local value is reserved
        // for intentional muted, status, validation, or plugin-supplied treatments.
        if (color is not null) block.Foreground = color;
        return block;
    }
    public static TextBox Input(string text = "", bool multiline = false)
    {
        var input = new TextBox
        {
            Text = text, AcceptsReturn = multiline, TextWrapping = multiline ? TextWrapping.Wrap : TextWrapping.NoWrap,
            VerticalScrollBarVisibility = multiline ? ScrollBarVisibility.Auto : ScrollBarVisibility.Hidden,
            MinHeight = multiline ? 110 : 32
        };
        TextContextMenus.Attach(input);
        return input;
    }
    public static TextBox Json(string text = "")
    {
        var box = Input(text, true); box.FontFamily = new FontFamily("Cascadia Mono, Consolas");
        box.TextWrapping = TextWrapping.NoWrap; box.HorizontalScrollBarVisibility = ScrollBarVisibility.Auto;
        return box;
    }
    public static Button Button(string text, Action action, bool primary = false)
    {
        var button = new Button { Content = text };
        if (primary && Application.Current?.Resources[PrimaryButtonStyleKey] is Style primaryStyle) button.Style = primaryStyle;
        button.Click += (_, _) => action(); return button;
    }

    /// <summary>
    /// Creates a local layout or automation style without discarding the shared appearance,
    /// selection, hover, disabled-state, or control-template contract for that control type.
    /// </summary>
    public static Style ExtendStyle<T>() where T : FrameworkElement
    {
        var style = new Style(typeof(T));
        if (Application.Current?.TryFindResource(typeof(T)) is Style shared) style.BasedOn = shared;
        return style;
    }
    public static WrapPanel Bar(params UIElement[] elements)
    {
        var panel = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 12) };
        foreach (var element in elements) panel.Children.Add(element); return panel;
    }
    public static DockPanel Page(string title, string description, UIElement body, params UIElement[] buttons)
    {
        var panel = new DockPanel { Margin = new Thickness(20) };
        var heading = new StackPanel(); heading.Children.Add(Text(title, 23));
        heading.Children.Add(Text(description, 13, Muted));
        if (buttons.Length > 0) heading.Children.Add(Bar(buttons));
        DockPanel.SetDock(heading, Dock.Top); panel.Children.Add(heading); panel.Children.Add(body); return panel;
    }
    public static DataGrid Grid(params (string Label, string Property, double Width)[] columns)
    {
        var grid = new DataGrid();
        foreach (var (label, property, width) in columns)
            grid.Columns.Add(new DataGridTextColumn { Header = label, Binding = new System.Windows.Data.Binding(property), Width = new DataGridLength(width, DataGridLengthUnitType.Star) });
        return grid;
    }
    public static string Serialize(object value) => JsonSerializer.Serialize(value, CerebralJson.Options);
    public static T Parse<T>(string text) => JsonSerializer.Deserialize<T>(text, CerebralJson.Options) ?? throw new ArgumentException("Enter a JSON value.");
    // Local assembly metadata and public project information; this view never reads service or user settings.
    public static FrameworkElement AboutContent(string application, System.Reflection.Assembly hostingAssembly)
    {
        ArgumentNullException.ThrowIfNull(hostingAssembly);
        if (application is not ("orchestrator" or "integrator"))
            throw new ArgumentOutOfRangeException(nameof(application));
        string? ReadAttribute<T>(Func<T, string?> value) where T : System.Attribute =>
            hostingAssembly.GetCustomAttributes(typeof(T), false).OfType<T>().Select(value).FirstOrDefault();
        var product = ReadAttribute<System.Reflection.AssemblyProductAttribute>(value => value.Product)
            ?? hostingAssembly.GetName().Name ?? Branding.ApplicationName;
        var informational = ReadAttribute<System.Reflection.AssemblyInformationalVersionAttribute>(value => value.InformationalVersion);
        var fileVersion = ReadAttribute<System.Reflection.AssemblyFileVersionAttribute>(value => value.Version);
        var assemblyVersion = hostingAssembly.GetName().Version?.ToString();
        var content = new StackPanel { MaxWidth = 760, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(20) };
        System.Windows.Automation.AutomationProperties.SetAutomationId(content, "CerebralAboutContent");
        System.Windows.Automation.AutomationProperties.SetName(content, "About " + Branding.DisplayName(application));
        content.Children.Add(Branding.Header(application, "A Windows workspace for AI research and development.", compact: true));
        var version = Text("Version " + (informational ?? fileVersion ?? assemblyVersion ?? "not recorded"), 14);
        version.Margin = new Thickness(0, 14, 0, 4);
        System.Windows.Automation.AutomationProperties.SetAutomationId(version, "CerebralAboutVersion");
        content.Children.Add(version);
        content.Children.Add(Text("Product: " + product, 12, Muted));
        content.Children.Add(Text("File version " + (fileVersion ?? "not recorded") +
            " · Assembly version " + (assemblyVersion ?? "not recorded"), 12, Muted));
        var credits = Text("Created by Shayne Winter", 15); credits.FontWeight = FontWeights.SemiBold;
        credits.Margin = new Thickness(0, 10, 0, 4); content.Children.Add(credits);
        content.Children.Add(Text("Development assistance: OpenAI Codex and Pegasus Cerebral.", 13));
        var linkNotice = Text("", 12, Muted); linkNotice.Visibility = Visibility.Collapsed;
        foreach (var (id, label) in new[]
        {
            ("website", "Website · cerebral.shaynewinter.com"),
            ("source", "Source repository · github.com/swpegasus/Pegasus-Cerebral"),
            ("portfolio", "Shayne Winter · shaynewinter.com")
        })
        {
            // Only these fixed HTTPS destinations can be launched; ignore a routed event's URI.
            var uri = new Uri(id switch
            {
                "website" => "https://cerebral.shaynewinter.com/",
                "source" => "https://github.com/swpegasus/Pegasus-Cerebral",
                "portfolio" => "https://shaynewinter.com/",
                _ => throw new InvalidOperationException("Unknown About link.")
            }, UriKind.Absolute);
            var link = new System.Windows.Documents.Hyperlink(new System.Windows.Documents.Run(label))
                { NavigateUri = uri, Foreground = Accent };
            System.Windows.Automation.AutomationProperties.SetAutomationId(link, "CerebralAboutLink." + id);
            System.Windows.Automation.AutomationProperties.SetName(link, label);
            System.Windows.Automation.AutomationProperties.SetHelpText(link, uri.AbsoluteUri);
            link.ToolTip = uri.AbsoluteUri;
            link.RequestNavigate += (_, request) =>
            {
                request.Handled = true;
                try
                {
                    using var browser = System.Diagnostics.Process.Start(
                        new System.Diagnostics.ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
                    linkNotice.Text = ""; linkNotice.Visibility = Visibility.Collapsed;
                }
                catch (Exception error) when (error is Win32Exception or InvalidOperationException or NotSupportedException or System.Security.SecurityException)
                {
                    linkNotice.Text = "The browser could not be opened. Use the address shown in the link: " + uri.AbsoluteUri;
                    linkNotice.Visibility = Visibility.Visible;
                }
            };
            var row = new TextBlock { FontSize = 13, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 3) };
            row.Inlines.Add(link); content.Children.Add(row);
        }
        content.Children.Add(linkNotice);
        var license = Text("Pegasus Community License 1.0", 15); license.FontWeight = FontWeights.SemiBold;
        license.Margin = new Thickness(0, 18, 0, 8); content.Children.Add(license);
        content.Children.Add(Text("Genuine personal use qualifies for free community permission. Organizations with combined group gross revenue and receipts below US$10 million over the preceding twelve months also qualify, including for commercial use.", 13));
        content.Children.Add(Text("Use for or on behalf of a group at or above US$10 million requires a separate written commercial agreement signed by Shayne Winter before that use. Working as an individual or contractor does not bypass that threshold; payment alone does not create permission.", 13));
        content.Children.Add(Text("Distributed copies must include the license and applicable notices and identify modifications. Third-party software, models, runtimes and artwork retain their own terms. Your data and outputs are not automatically placed under this license.", 13));
        content.Children.Add(Text("This is a summary; the full license governs. Keep it and applicable third-party notices with any distribution.", 12, Muted));
        var scroll = new ScrollViewer { Content = content, Background = Surface, Foreground = Ink,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        System.Windows.Automation.AutomationProperties.SetAutomationId(scroll, "CerebralAboutPage");
        System.Windows.Automation.AutomationProperties.SetName(scroll, "About " + Branding.DisplayName(application));
        return scroll;
    }

    public static void ShowAbout(Window owner, string application, System.Reflection.Assembly hostingAssembly)
    {
        ArgumentNullException.ThrowIfNull(owner);
        owner.VerifyAccess();
        foreach (Window existing in owner.OwnedWindows)
            if (ReferenceEquals(existing.Tag, AboutWindowTag))
            {
                if (existing.WindowState == WindowState.Minimized) existing.WindowState = WindowState.Normal;
                existing.Activate(); return;
            }
        var window = new Window { Owner = owner, Icon = Branding.IconFor(application),
            Title = "About " + Branding.DisplayName(application), Width = 680, Height = 660, MinWidth = 420, MinHeight = 360,
            ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.CenterOwner, Tag = AboutWindowTag };
        System.Windows.Automation.AutomationProperties.SetAutomationId(window, "CerebralAboutWindow");
        var layout = new DockPanel { Background = Surface };
        var close = Button("Close", window.Close); close.IsCancel = true;
        close.MinHeight = 28; close.Padding = new Thickness(12, 4, 12, 4); close.FontSize = 13;
        close.HorizontalAlignment = HorizontalAlignment.Right; close.Margin = new Thickness(12);
        System.Windows.Automation.AutomationProperties.SetAutomationId(close, "CloseCerebralAbout");
        DockPanel.SetDock(close, Dock.Bottom); layout.Children.Add(close);
        layout.Children.Add(AboutContent(application, hostingAssembly)); window.Content = layout;
        WindowSizing.Attach(window); window.Show();
    }

    public static void ShowText(Window owner, string title, string text) => ShowText(owner, title, text, wrap: false);

    public static void ShowText(Window owner, string title, string text, bool wrap)
    {
        var box = wrap ? Input(text, multiline: true) : Json(text);
        box.IsReadOnly = true;
        var window = new Window { Owner = owner, Icon = owner.Icon, Title = title, Width = 860, Height = 640, WindowStartupLocation = WindowStartupLocation.CenterOwner, Content = Ui.Page(title, "Read-only details", box) };
        WindowSizing.Attach(window); window.Show();
    }
}

public abstract class ConnectedWindow : Window
{
    private CerebralClient client = new();
    protected CerebralClient Client => IsConnected ? client : throw new InvalidOperationException(connectionOwner
        ? "Cerebral is offline. Use Advanced → Local recovery to inspect its connection, logs, and selected data folder."
        : "The service is unavailable. Open Pegasus Cerebral Configuration to configure or start it, then reconnect.");
    protected CerebralClient CreateIndependentClient() => IsConnected ? activeConnection.CreateClient() : throw new InvalidOperationException("Connect before opening an independent service client.");
    protected readonly TabControl Tabs = new() { Margin = new Thickness(20, 0, 20, 0), BorderBrush = Ui.Line, Background = Ui.Surface };
    protected readonly TextBlock Status = Ui.Text("Not connected", 13, Ui.Muted);
    protected readonly CancellationTokenSource Lifetime = new();
    private readonly bool connectionOwner;
    private readonly SemaphoreSlim operation = new(1, 1);
    private readonly TaskCompletionSource<DesktopStartupOutcome> initialConnection =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private CancellationTokenSource? stream;
    private ConnectionPreferences activeConnection = new();
    private string appliedRevision = "";
    private bool inspectionMode;
    private bool initialConnectStarted;
    private DateTimeOffset retryAt;
    private ConnectionSnapshot? existingWorkerConnection;
    private string? existingWorkerInstallationId;
    private ExistingWorkerDesktopOptions? existingConfigurationTarget;
    internal Func<string, DesktopApplicationIdentity, bool> ConfigurationActivator { get; set; } =
        DesktopApplicationInstance.TryActivateExisting;
    private readonly List<Button> connectionManagementActions = [];
    private readonly List<TextBlock> connectionOwnershipLabels = [];
    private ConnectionSnapshot CurrentConnectionSnapshot() => existingWorkerConnection ?? ConnectionStore.Read();
    protected bool IsConnected { get; private set; }
    public bool ServiceConnected => IsConnected;
    /// <summary>The immutable fixed binding selected before initial connection, or null for normal discovery.</summary>
    public ExistingLocalWorkerAttachment? ExistingWorkerAttachment { get; private set; }
    public string ServiceStatusText => Status.Text;
    public event EventHandler? ServiceStatusChanged;
    public event EventHandler<DesktopStartupProgress>? StartupProgressChanged;
    protected Task<DesktopStartupOutcome> InitialConnectionSettled => initialConnection.Task;
    protected string ServiceUrl => activeConnection.Url;
    protected ConnectionPreferences ServiceConnection => activeConnection;
    protected bool ServiceIsLocal => activeConnection.Transport is "pipe" or "installed-pipe" or "installed-user-pipe" || Uri.TryCreate(ServiceUrl, UriKind.Absolute, out var endpoint) && endpoint.IsLoopback;
    protected string ServiceConnectionIdentity => activeConnection.Transport switch
    {
        "pipe" => activeConnection.LocalEndpoint!.InstanceId,
        "installed-pipe" => "installed-owner-pipe",
        "installed-user-pipe" => "installed-user-pipe",
        _ => activeConnection.Url
    };

    protected ConnectedWindow(string application, string description, bool compactHeader = false, bool persistWindowPreferences = true)
    {
        // WPF does not automatically apply the implicit Window style to derived window types.
        // Supply inherited ink at the host boundary so plain plugin TextBlocks follow the theme,
        // while text inside selected/disabled controls still inherits that control's foreground.
        SetResourceReference(Control.ForegroundProperty, DesktopThemeResourceKeys.TextBrush);
        connectionOwner = application == "Integrator";
        Title = Branding.DisplayName(application); Width = 1280; Height = 840; MinWidth = 900; MinHeight = 620;
        WindowSizing.Attach(this);
        WindowStartupLocation = WindowStartupLocation.CenterScreen; Icon = Branding.IconFor(application);
        if (persistWindowPreferences) WindowPreferenceStore.Attach(this, application, Tabs);
        var layout = new DockPanel();
        if (compactHeader && !connectionOwner)
        {
            AddCompactHeader(layout, application, description);
        }
        else
        {
            AddStandardHeader(layout, application, description);
        }
        var statusFrame = new Border { Padding = new Thickness(25, 11, 25, 11), BorderBrush = Ui.Line, BorderThickness = new Thickness(0, 1, 0, 0), Background = Ui.Surface, Child = Status };
        DockPanel.SetDock(statusFrame, Dock.Bottom); layout.Children.Add(statusFrame); layout.Children.Add(Tabs); Content = layout;
        System.Windows.Automation.AutomationProperties.SetAutomationId(Tabs, "ApplicationTabs");
        System.Windows.Automation.AutomationProperties.SetAutomationId(Status, "ConnectionStatus");
        Loaded += (_, _) => _ = BeginInitialConnectionAsync();
        Closed += (_, _) => { Lifetime.Cancel(); StopStream(); client.Dispose(); };
    }

    /// <summary>Starts the normal initial connection once, including before this window is shown.</summary>
    protected Task<DesktopStartupOutcome> BeginInitialConnectionAsync()
    {
        Dispatcher.VerifyAccess();
        if (!inspectionMode && !initialConnectStarted)
        {
            initialConnectStarted = true;
            _ = StartInitialConnectionAsync();
        }
        return initialConnection.Task;
    }

    /// <summary>Binds this window permanently to one already-running private Worker. No saved
    /// connection discovery, automatic startup or replacement-instance adoption is permitted.</summary>
    public void UseExistingLocalWorker(LocalWorkerEndpoint endpoint, string dataDirectory, string installationId) =>
        UseExistingLocalWorker(endpoint, dataDirectory, installationId, null);

    public void UseExistingLocalWorker(LocalWorkerEndpoint endpoint, string dataDirectory, string installationId,
        string? configurationStateDirectory)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        endpoint.Validate();
        var preferences = ConnectionPreferences.ForLocalPipe(endpoint, dataDirectory, autoStart: false);
        if (string.IsNullOrWhiteSpace(installationId) || installationId.Length > 256 || installationId.Any(char.IsControl))
            throw new ArgumentException("An exact Worker installation identity is required.");
        if (initialConnectStarted || IsLoaded || inspectionMode || existingWorkerConnection is not null)
            throw new InvalidOperationException("Select the existing Worker once, before loading the window.");
        // The companion state is explicit launch metadata, never a path inferred from this
        // window's storage or binaries. It grants activation only, not launch or retargeting.
        var configurationTarget = configurationStateDirectory is null ? null :
            ExistingWorkerDesktopOptions.FromArguments(["--attach-existing", endpoint.PipeName,
                endpoint.InstanceId, preferences.LocalDataDirectory!, configurationStateDirectory, installationId]);
        ExistingWorkerDesktopPresentation.Configure(this);
        existingWorkerConnection = new(preferences, "attached:" + endpoint.InstanceId, true, null);
        existingWorkerInstallationId = installationId;
        existingConfigurationTarget = configurationTarget;
        ExistingWorkerAttachment = new(endpoint, preferences.LocalDataDirectory!, installationId);
        foreach (var action in connectionManagementActions)
        {
            action.IsEnabled = configurationTarget is not null;
            action.ToolTip = configurationTarget is null
                ? "This private interface uses a fixed existing Worker. Configure it through its owning workspace."
                : "Show the already-open Configuration for this existing Worker.";
        }
        foreach (var label in connectionOwnershipLabels) label.Text = "Attached to an existing Worker";
    }

    /// <summary>
    /// Detach the standard composition after derived controls have been initialized. Connection,
    /// lifetime, status events and the existing tab selection remain owned by this window.
    /// </summary>
    protected void ComposeApplicationSurface(Func<TabControl, TextBlock, FrameworkElement> compose)
    {
        ArgumentNullException.ThrowIfNull(compose);
        if (Tabs.Parent is Panel parent) parent.Children.Remove(Tabs);
        if (Status.Parent is Border frame) frame.Child = null;
        Content = compose(Tabs, Status);
    }

    private void AddCompactHeader(DockPanel layout, string application, string description)
    {
        var header = new Grid { Margin = new Thickness(20, 8, 20, 6) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        System.Windows.Automation.AutomationProperties.SetAutomationId(header, "ApplicationHeader");

        var branding = Branding.Header(application, description, compact: true);
        branding.Margin = new Thickness(0, 0, 20, 0);
        System.Windows.Automation.AutomationProperties.SetAutomationId(branding, "ApplicationBranding");
        header.Children.Add(branding);

        var connection = new StackPanel { HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        var connectionLabel = Ui.Text("Connection managed by Configuration", 12, Ui.Muted);
        connectionLabel.Margin = new Thickness(0, 0, 0, 2);
        connectionLabel.HorizontalAlignment = HorizontalAlignment.Left;
        connectionLabel.ToolTip = "Connection managed by Pegasus Cerebral Configuration";
        connectionOwnershipLabels.Add(connectionLabel);
        System.Windows.Automation.AutomationProperties.SetAutomationId(connectionLabel, "ConnectionOwnership");
        connection.Children.Add(connectionLabel);

        var reconnect = Ui.Button("Reconnect", () => Run(ConnectAsync));
        System.Windows.Automation.AutomationProperties.SetAutomationId(reconnect, "ReconnectService");
        var manage = Ui.Button("Configuration", () => Run(OpenConnectionConfigurationAsync));
        manage.ToolTip = "Open Pegasus Cerebral Configuration to configure the connection, models, agents and local services";
        connectionManagementActions.Add(manage);
        System.Windows.Automation.AutomationProperties.SetAutomationId(manage, "OpenConfiguration");
        System.Windows.Automation.AutomationProperties.SetName(manage, "Open Pegasus Cerebral Configuration");
        var actions = Ui.Bar(reconnect, manage);
        actions.Margin = new Thickness(0);
        connection.Children.Add(actions);
        Grid.SetColumn(connection, 1); header.Children.Add(connection);

        DockPanel.SetDock(header, Dock.Top); layout.Children.Add(header);
    }

    private void AddStandardHeader(DockPanel layout, string application, string description)
    {
        var header = new StackPanel { Margin = new Thickness(25, 14, 25, 8) };
        System.Windows.Automation.AutomationProperties.SetAutomationId(header, "ApplicationHeader");
        var brandBar = new DockPanel();
        if (!connectionOwner)
        {
            var manage = Ui.Button("Open Pegasus Cerebral Configuration", () => Run(OpenConnectionConfigurationAsync));
            manage.VerticalAlignment = VerticalAlignment.Top; manage.Margin = new Thickness(24, 2, 0, 0);
            manage.ToolTip = "Configure the connection, models, agents and local services";
            connectionManagementActions.Add(manage);
            System.Windows.Automation.AutomationProperties.SetAutomationId(manage, "OpenConfiguration");
            DockPanel.SetDock(manage, Dock.Right); brandBar.Children.Add(manage);
        }
        var branding = Branding.Header(application, description);
        System.Windows.Automation.AutomationProperties.SetAutomationId(branding, "ApplicationBranding");
        brandBar.Children.Add(branding); header.Children.Add(brandBar);
        if (!connectionOwner)
        {
            var connectionLabel = Ui.Text("Connection managed by Pegasus Cerebral Configuration", 12, Ui.Muted);
            connectionLabel.Margin = new Thickness(0, 0, 20, 0);
            connectionLabel.VerticalAlignment = VerticalAlignment.Center;
            connectionOwnershipLabels.Add(connectionLabel);
            var reconnect = Ui.Button("Reconnect", () => Run(ConnectAsync));
            System.Windows.Automation.AutomationProperties.SetAutomationId(reconnect, "ReconnectService");
            var connectionBar = Ui.Bar(connectionLabel, reconnect);
            connectionBar.Margin = new Thickness(0, 10, 0, 0);
            header.Children.Add(connectionBar);
        }
        DockPanel.SetDock(header, Dock.Top); layout.Children.Add(header);
    }
    protected void Tab(string title, UIElement content) => Tabs.Items.Add(Ui.TabPage(title, content, new Thickness(16, 9, 16, 9)));
    protected async void Run(Func<Task> action)
    {
        if (!await operation.WaitAsync(0)) { SetStatus("An operation is in progress. Wait for its result before starting another."); return; }
        try { await action(); }
        catch (OperationCanceledException) when (Lifetime.IsCancellationRequested) { }
        catch (Exception ex) { SetStatus(ex.Message, true); }
        finally { operation.Release(); }
    }
    protected void SetStatus(string text, bool error = false)
    {
        Status.Text = text; Status.Foreground = error ? Ui.ErrorText : Ui.Muted;
        ServiceStatusChanged?.Invoke(this, EventArgs.Empty);
    }
    protected void ReportStartupProgress(string stage, string message, bool error = false)
    {
        try { StartupProgressChanged?.Invoke(this, new(stage, message, error)); }
        catch { /* A diagnostic surface must never destabilize the application it observes. */ }
    }
    private async Task StartInitialConnectionAsync()
    {
        DesktopStartupOutcome outcome;
        var entered = false;
        try
        {
            await operation.WaitAsync(Lifetime.Token);
            entered = true;
            ReportStartupProgress("Service", "Checking the configured local service…");
            // An attached Worker is an immutable caller-supplied binding, not discovery authority.
            if (existingWorkerConnection is null) await BeforeInitialConnectAsync(ConnectionStore.Read());
            ReportStartupProgress("Service", "Connecting to the configured Cerebral service…");
            await ConnectAsync();
            outcome = new(true);
            ReportStartupProgress("Connected", "The Worker connection and initial application data are ready; Main startup checks continue.");
        }
        catch (OperationCanceledException error) when (Lifetime.IsCancellationRequested)
        {
            outcome = new(false, "Startup was cancelled.") { Error = error };
        }
        catch (Exception error)
        {
            var diagnostic = error.Message;
            SetStatus(diagnostic, true);
            outcome = new(false, diagnostic) { Error = error };
            ReportStartupProgress("Offline", diagnostic, true);
        }
        finally
        {
            if (entered) operation.Release();
        }
        initialConnection.TrySetResult(outcome);
        if (!Lifetime.IsCancellationRequested && !inspectionMode) _ = MonitorConnectionAsync();
    }
    protected async Task<T> Get<T>(string operationName, object? arguments = null, string? profileId = null) =>
        await Client.GetAsync<T>(operationName, arguments, profileId, Lifetime.Token);
    protected async Task<CommandResult> Command(string operationName, object? arguments = null, string? profileId = null)
    {
        var result = await Client.CommandAsync(operationName, arguments, profileId, Lifetime.Token);
        if (!result.Success) throw new InvalidOperationException(result.Error ?? "Operation failed.");
        return result;
    }

    protected Task ConnectAsync() => ConnectCoreAsync(CurrentConnectionSnapshot(), true);

    protected async Task OpenConnectionConfigurationAsync()
    {
        if (existingWorkerConnection is not null)
        {
            var target = existingConfigurationTarget ?? throw new InvalidOperationException(
                "Configuration is not configured for this private interface. Open it from the owning workspace, then relaunch this interface with its explicit Configuration state directory.");
            var activate = ConfigurationActivator;
            // Named-object readiness and request serialization are bounded, but must not
            // block the UI dispatcher. Success acknowledges submission, not foreground focus.
            var requested = await Task.Run(() => activate(target.GetApplicationId("Integrator"), target.ApplicationIdentity), Lifetime.Token);
            Lifetime.Token.ThrowIfCancellationRequested();
            if (!requested)
                throw new InvalidOperationException(
                    "Configuration is not open for this interface's fixed Worker binding, or its owner could not be verified. Open the matching Configuration from the owning workspace, then try Settings again.");
            SetStatus("Activation requested for Configuration attached to this Worker.");
            return;
        }
        ManagementProcess.OpenApplication("Integrator");
    }

    private void StopStream() { stream?.Cancel(); stream?.Dispose(); stream = null; }

    private long appearancePresetRevision = -1;

    private async Task<string?> HydrateAppearancePresetAsync(CancellationToken token)
    {
        try
        {
            var result = await client.CommandAsync("interface.theme.get", cancellationToken: token);
            if (!result.Success)
            {
                // Older workers can still render the built-in preset. A malformed new response does
                // not invalidate the already verified service connection or replace readable colors.
                if (result.Error == "Unknown command: interface.theme.get")
                {
                    Ui.ApplyThemePreset(DesktopThemePresetPolicy.BlueWhite);
                    return null;
                }
                return "Appearance preset could not be loaded: " + result.Error;
            }
            ApplyAppearancePresetState(result.Data);
            return null;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception error) { return "Appearance preset could not be loaded: " + error.Message; }
    }

    private void ApplyAppearancePresetState(JsonElement data)
    {
        var revision = data.GetProperty("revision").GetInt64();
        if (revision < 0) throw new InvalidDataException("Appearance preset revision is invalid.");
        if (revision < appearancePresetRevision) return;
        var preset = DesktopThemePresetPolicy.Parse(data.GetProperty("preset"));
        Ui.ApplyThemePreset(preset);
        appearancePresetRevision = revision;
    }

    // Initial startup prepares a verified local declaration before Configuration connects. Apply the
    // same refusal to manual reconnects and the monitor, so failed discovery cannot probe default HTTP.
    private static ConnectionPreferences RequireConnectionPreferences(ConnectionSnapshot saved, bool configuration)
    {
        var value = saved.Preferences ?? throw new InvalidOperationException(saved.Error ?? "The saved Cerebral connection is unavailable.");
        if (configuration && !saved.Saved)
            throw new InvalidOperationException("The local Cerebral connection is not ready. Use Advanced → Local recovery to retry or inspect the selected data folder.");
        return value;
    }

    private async Task ConnectCoreAsync(ConnectionSnapshot saved, bool watchEvents)
    {
        if (existingWorkerConnection is not null && !ReferenceEquals(saved, existingWorkerConnection))
            throw new InvalidOperationException("An attached interface cannot switch its existing Worker binding.");
        // Every reconnect replaces the client, even when its endpoint is unchanged. Refuse before
        // tearing down the original transport so pending or unconfirmed mutations remain retryable.
        ConnectionPreferences preferences;
        bool changed;
        try
        {
            preferences = RequireConnectionPreferences(saved, connectionOwner && !inspectionMode);
            changed = activeConnection.Transport != preferences.Transport || activeConnection.LocalEndpoint != preferences.LocalEndpoint ||
                activeConnection.LocalDataDirectory != preferences.LocalDataDirectory || activeConnection.Url != preferences.Url ||
                activeConnection.AccessKey != preferences.AccessKey;
            ValidateConnectionChange();
        }
        catch (Exception error)
        {
            SetStatus("Connection change deferred: " + error.Message, true);
            throw;
        }
        StopStream(); IsConnected = false; appliedRevision = saved.Revision;
        try
        {
            if (changed) ConnectionChanged();
            activeConnection = preferences;
            client.Dispose(); client = preferences.CreateClient();
            SetStatus("Connecting…");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Lifetime.Token); timeout.CancelAfter(TimeSpan.FromSeconds(12));
            var info = await client.GetAsync<HostInfo>("health", cancellationToken: timeout.Token);
            preferences.ValidateHost(info);
            if (existingWorkerInstallationId is { } expectedInstallation && info.InstallationId != expectedInstallation)
                throw new InvalidOperationException("The existing Worker returned a different installation identity.");
            appearancePresetRevision = -1;
            var appearanceDiagnostic = await HydrateAppearancePresetAsync(timeout.Token);
            IsConnected = true;
            // A verified replacement starts a new feed generation. Reset transient projections
            // before the one existing hydration; neither callback may replay uncertain effects.
            await RefreshAfterReconnectAsync();
            await ConnectedAsync(info);
            SetStatus($"Connected to {info.Name} · {info.Architecture} · v{info.Version}" +
                (appearanceDiagnostic is null ? "" : " · " + appearanceDiagnostic), appearanceDiagnostic is not null);
            if (watchEvents)
            {
                stream = CancellationTokenSource.CreateLinkedTokenSource(Lifetime.Token);
                _ = WatchAsync(client, stream.Token);
            }
        }
        catch (Exception ex)
        {
            IsConnected = false; retryAt = DateTimeOffset.UtcNow.AddSeconds(5);
            if (Lifetime.IsCancellationRequested) throw;
            var recovery = existingWorkerConnection is not null
                ? "Retry this same Worker after its owning workspace restores it; this interface does not start or replace it."
                : connectionOwner ? "Use Advanced → Local recovery to inspect or retry."
                : "Open Pegasus Cerebral Configuration to inspect or start it.";
            var message = saved.Error ?? $"Service unavailable: {ex.Message} {recovery} The saved connection will be retried.";
            SetStatus(message, true); throw new InvalidOperationException(message, ex);
        }
    }
    private async Task MonitorConnectionAsync()
    {
        try
        {
            while (!Lifetime.IsCancellationRequested && !inspectionMode)
            {
                await Task.Delay(TimeSpan.FromSeconds(2), Lifetime.Token);
                if (inspectionMode) return;
                var saved = CurrentConnectionSnapshot();
                if (saved.Revision == appliedRevision && (IsConnected || DateTimeOffset.UtcNow < retryAt)) continue;
                if (!await operation.WaitAsync(0, Lifetime.Token)) continue;
                try { await ConnectCoreAsync(saved, true); }
                catch (OperationCanceledException) when (Lifetime.IsCancellationRequested) { return; }
                catch (Exception error) { SetStatus(IsConnected ? "Connection change deferred: " + error.Message : saved.Error ?? error.Message, true); }
                finally { operation.Release(); }
            }
        }
        catch (OperationCanceledException) when (Lifetime.IsCancellationRequested) { }
    }
    public async Task<object> InspectServiceUiAsync(string endpoint, string? sessionId)
    {
        EnterInspectionMode(TimeSpan.FromSeconds(45));
        await ConnectCoreAsync(new(new() { Url = endpoint, AccessKey = "" }, "inspection", false, null), false);
        return await InspectLoadedStateAsync(sessionId);
    }
    public async Task<object> InspectServiceUiAsync(LocalWorkerEndpoint endpoint, string dataDirectory, string? sessionId)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        endpoint.Validate();
        var connection = ConnectionPreferences.ForLocalPipe(endpoint, dataDirectory, autoStart: false);
        EnterInspectionMode(TimeSpan.FromSeconds(45));
        await ConnectCoreAsync(new(connection, "pipe-inspection", false, null), false);
        return await InspectLoadedStateAsync(sessionId);
    }
    /// <summary>Prevents automatic connection and monitoring for an isolated, caller-owned UI acceptance window.</summary>
    protected void EnterInspectionMode(TimeSpan? lifetime = null)
    {
        if (existingWorkerConnection is not null)
            throw new InvalidOperationException("An attached interface retains its normal event stream and monitoring lifetime.");
        inspectionMode = true;
        initialConnectStarted = true;
        initialConnection.TrySetResult(new(false, "Inspection mode does not start the saved service connection."));
        if (lifetime is { } duration) Lifetime.CancelAfter(duration);
    }
    protected abstract Task<object> InspectLoadedStateAsync(string? sessionId);
    private async Task WatchAsync(CerebralClient watchedClient, CancellationToken token)
    {
        try
        {
            await foreach (var item in watchedClient.EventsAsync(token))
            {
                if (token.IsCancellationRequested || !ReferenceEquals(client, watchedClient)) return;
                if (item.Type == "interface.theme.updated")
                {
                    try { ApplyAppearancePresetState(item.Data); }
                    catch (Exception error) { SetStatus("Appearance preset update was rejected: " + error.Message, true); }
                }
                await OnEventAsync(item);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
        catch (Exception) { if (Lifetime.IsCancellationRequested || token.IsCancellationRequested) return; }
        if (!token.IsCancellationRequested && ReferenceEquals(client, watchedClient))
        {
            IsConnected = false; retryAt = DateTimeOffset.UtcNow.AddSeconds(5);
            SetStatus(existingWorkerConnection is null
                ? "Service connection unavailable. Retrying the saved Configuration connection in five seconds."
                : "Attached Worker unavailable. Retrying the same fixed Worker binding in five seconds.", true);
        }
    }
    protected virtual Task BeforeInitialConnectAsync(ConnectionSnapshot saved) => Task.CompletedTask;
    /// <summary>Read-only preflight; rejection preserves the current client, stream and connection identity.</summary>
    protected virtual void ValidateConnectionChange() { }
    protected virtual void ConnectionChanged() { }
    protected virtual Task OnEventAsync(ServiceEvent item) => Task.CompletedTask;
    /// <summary>Reset transient feed state after host verification, before the single ConnectedAsync hydration.</summary>
    protected virtual Task RefreshAfterReconnectAsync() => Task.CompletedTask;
    protected abstract Task ConnectedAsync(HostInfo info);
}

/// <summary>
/// Displays one fixed token window as a prompt/history remainder and an adjustable response reservation.
/// Any reasoning reservation is supplied separately so moving the slider never changes reasoning settings.
/// </summary>
public sealed class TokenBalanceControl : UserControl
{
    private readonly Slider response = new()
    {
        Minimum = AgentTokenLimits.MinimumResponseTokens,
        Maximum = 511,
        Value = 128,
        TickFrequency = 1,
        IsSnapToTickEnabled = true,
        AutoToolTipPlacement = System.Windows.Controls.Primitives.AutoToolTipPlacement.TopLeft,
        AutoToolTipPrecision = 0,
        Margin = new Thickness(0, 3, 12, 3)
    };
    private readonly TextBlock allocation = Ui.Text("", 13);
    private readonly TextBlock mode = Ui.Text("", 12, Ui.Muted);
    private bool configuring;

    public TokenBalanceControl()
    {
        var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 13) };
        allocation.Margin = new Thickness(0, 0, 0, 1);
        mode.Margin = new Thickness(0);
        panel.Children.Add(allocation);
        panel.Children.Add(response);
        var direction = new Grid { Margin = new Thickness(0, -2, 12, 2) };
        direction.ColumnDefinitions.Add(new ColumnDefinition()); direction.ColumnDefinitions.Add(new ColumnDefinition());
        var historyLabel = Ui.Text("More conversation & source", 11, Ui.Muted);
        var responseLabel = Ui.Text("Longer response", 11, Ui.Muted); responseLabel.HorizontalAlignment = HorizontalAlignment.Right; Grid.SetColumn(responseLabel, 1);
        direction.Children.Add(historyLabel); direction.Children.Add(responseLabel); panel.Children.Add(direction);
        panel.Children.Add(mode);
        Content = panel;
        response.ValueChanged += (_, _) =>
        {
            UpdateAllocation();
            if (!configuring) ResponseTokensChanged?.Invoke(this, EventArgs.Empty);
        };
        Configure(512, 128, 0, true, "Custom agent default");
    }

    public event EventHandler? ResponseTokensChanged;
    public int TotalTokens { get; private set; }
    public int FixedReservedTokens { get; private set; }
    public int PromptTokens => Math.Max(0, TotalTokens - ResponseTokens - FixedReservedTokens - AgentTokenLimits.ContextSafetyTokens);
    public int ResponseTokens
    {
        get => (int)Math.Round(response.Value);
        set => response.Value = Math.Clamp(value, (int)response.Minimum, (int)response.Maximum);
    }
    public bool IsBalanceEditable => response.IsEnabled;

    public void Configure(int totalTokens, int responseTokens, int fixedReservedTokens, bool editable, string status)
    {
        if (totalTokens < 2) throw new ArgumentOutOfRangeException(nameof(totalTokens));
        if (fixedReservedTokens < 0 || fixedReservedTokens >= totalTokens - 1) throw new ArgumentOutOfRangeException(nameof(fixedReservedTokens));
        configuring = true;
        try
        {
            TotalTokens = totalTokens;
            FixedReservedTokens = fixedReservedTokens;
            response.Minimum = Math.Min(AgentTokenLimits.MinimumResponseTokens, totalTokens - fixedReservedTokens - 1);
            var safeMaximum = Math.Min(AgentTokenLimits.MaximumResponseTokens,
                totalTokens - fixedReservedTokens - AgentTokenLimits.ContextSafetyTokens - AgentTokenLimits.MinimumPromptTokens - 1);
            response.Maximum = Math.Max(response.Minimum, safeMaximum);
            response.SmallChange = 1;
            response.LargeChange = Math.Max(32, totalTokens / 20);
            response.Value = Math.Clamp(responseTokens, (int)response.Minimum, (int)response.Maximum);
            response.IsEnabled = editable;
            mode.Text = status;
            UpdateAllocation();
        }
        finally { configuring = false; }
    }

    private void UpdateAllocation()
    {
        var fixedText = FixedReservedTokens > 0 ? $" · {FixedReservedTokens:N0} reasoning reserved separately" : "";
        allocation.Text = $"Prompt & history up to {PromptTokens:N0} · Response {ResponseTokens:N0} · {AgentTokenLimits.ContextSafetyTokens:N0} safety · Total {TotalTokens:N0}{fixedText}";
    }
}

public sealed class FieldsDialog : Window
{
    private StackPanel fields = new();
    private readonly ContentControl fieldsHost = new();
    private TabControl? fieldTabs;
    private readonly Dictionary<string, TabItem> fieldTabItems = new(StringComparer.Ordinal);
    private bool canSubmit = true;
    private readonly Dictionary<string, Control> inputs = new(StringComparer.Ordinal);
    private readonly Dictionary<string, FrameworkElement> labels = new(StringComparer.Ordinal);
    private readonly TextBlock inlineStatus = Ui.Text("", 12, Ui.Muted);
    private readonly Button submitButton;
    private readonly Button closeButton;
    private Func<Task<bool>>? submitAsync;
    private bool submitting;
    private bool closeApproved;
    private bool submitOnClose = true;
    public FieldsDialog(Window owner, string title, string description = "", string submitLabel = "Save")
    {
        Owner = owner; Icon = owner.Icon; Title = title; Width = 680; Height = 670; MinWidth = 420; MinHeight = 320; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        WindowSizing.Attach(this);
        var root = new DockPanel { Margin = new Thickness(22) };
        var heading = new StackPanel(); heading.Children.Add(Ui.Text(title, 23)); heading.Children.Add(Ui.Text(description, 13, Ui.Muted));
        DockPanel.SetDock(heading, Dock.Top); root.Children.Add(heading);
        submitButton = Ui.Button(submitLabel, Submit, true);
        closeButton = Ui.Button("Cancel", DiscardDraftAndClose);
        var buttons = Ui.Bar(submitButton, closeButton);
        buttons.Margin = new Thickness(0, 15, 0, 0); DockPanel.SetDock(buttons, Dock.Bottom); root.Children.Add(buttons);
        inlineStatus.Visibility = Visibility.Collapsed;
        inlineStatus.Margin = new Thickness(0, 10, 0, 0);
        DockPanel.SetDock(inlineStatus, Dock.Bottom); root.Children.Add(inlineStatus);
        fieldsHost.Content = FieldScroller(fields);
        root.Children.Add(fieldsHost); Content = root;
        Closing += GuardClose;
    }
    private async void Submit()
    {
        if (await TrySubmitAsync()) { closeApproved = true; DialogResult = true; }
    }
    private async void GuardClose(object? sender, System.ComponentModel.CancelEventArgs args)
    {
        if (submitting) { args.Cancel = true; return; }
        if (closeApproved || submitAsync is null || !submitOnClose) return;
        args.Cancel = true;
        if (!await TrySubmitAsync()) return;
        closeApproved = true;
        _ = Dispatcher.BeginInvoke(Close);
    }
    private async Task<bool> TrySubmitAsync()
    {
        if (submitting || !canSubmit) return false;
        if (submitAsync is null) return true;
        submitting = true; submitButton.IsEnabled = false; closeButton.IsEnabled = false;
        try { return await submitAsync(); }
        catch (Exception error) { SetInlineStatus(error.Message, true); return false; }
        finally { submitting = false; submitButton.IsEnabled = canSubmit; closeButton.IsEnabled = true; }
    }
    private static ScrollViewer FieldScroller(StackPanel content) => new()
    {
        Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        VerticalContentAlignment = VerticalAlignment.Top
    };

    /// <summary>Opt-in field groups share one draft and the existing submit/cancel boundary.</summary>
    public FieldsDialog Tab(string id, string title)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id); ArgumentException.ThrowIfNullOrWhiteSpace(title);
        if (fieldTabs is null)
        {
            fieldTabs = new TabControl { Background = Ui.Surface, BorderThickness = new Thickness(0) };
            System.Windows.Automation.AutomationProperties.SetAutomationId(fieldTabs, "FieldsDialogTabs");
            if (fieldsHost.Content is ScrollViewer original) original.Content = null;
            fieldsHost.Content = fieldTabs;
            if (fields.Children.Count > 0)
                fieldTabs.Items.Add(Ui.TabPage("General", FieldScroller(fields)));
        }
        if (fieldTabItems.TryGetValue(id, out var existing))
        {
            fields = (StackPanel)((ScrollViewer)existing.Content).Content;
            return this;
        }
        fields = new StackPanel { VerticalAlignment = VerticalAlignment.Top };
        var tab = Ui.TabPage(title, FieldScroller(fields));
        System.Windows.Automation.AutomationProperties.SetAutomationId(tab, "FieldsDialogTab." + id);
        fieldTabItems.Add(id, tab); fieldTabs.Items.Add(tab);
        return this;
    }

    public FieldsDialog SetCanSubmit(bool enabled)
    {
        canSubmit = enabled; submitButton.IsEnabled = enabled && !submitting; return this;
    }

    public FieldsDialog SetChoices(string id, IEnumerable<string> values, string? selected)
    {
        var choice = (ComboBox)inputs[id];
        var items = values.ToList();
        if (selected is not null && !items.Contains(selected, StringComparer.Ordinal))
            throw new ArgumentException("The selected choice is unavailable.", nameof(selected));
        choice.ItemsSource = items; choice.SelectedItem = selected;
        return this;
    }

    public FieldsDialog Add(string id, string label, string value, bool multiline = false)
    {
        var heading = Ui.Text(label, 13); labels.Add(id, heading); fields.Children.Add(heading);
        var box = Ui.Input(value, multiline); box.Margin = new Thickness(0, 0, 12, 13);
        if (id == "json") { box.MinHeight = 450; box.FontFamily = new FontFamily("Consolas"); }
        inputs.Add(id, box); fields.Children.Add(box); return this;
    }
    public FieldsDialog Check(string id, string label, bool value)
    {
        var check = new CheckBox { Content = label, IsChecked = value, Margin = new Thickness(0, 6, 0, 15) };
        inputs.Add(id, check); fields.Children.Add(check); return this;
    }
    public FieldsDialog Choice(string id, string label, IEnumerable<string> values, string selected)
    {
        var heading = Ui.Text(label, 13); labels.Add(id, heading); fields.Children.Add(heading);
        var box = new ComboBox { ItemsSource = values.ToList(), SelectedItem = selected, Margin = new Thickness(0, 0, 12, 13) };
        inputs.Add(id, box); fields.Children.Add(box); return this;
    }
    public FieldsDialog MultiChoice(string id, string label, IEnumerable<string> values, IEnumerable<string> selected)
    {
        var heading = Ui.Text(label, 13); labels.Add(id, heading); fields.Children.Add(heading);
        var box = new ListBox
        {
            ItemsSource = values.ToList(), SelectionMode = SelectionMode.Multiple, MinHeight = 110, MaxHeight = 240,
            Margin = new Thickness(0, 0, 12, 13), BorderBrush = Ui.Line
        };
        var selectedValues = selected.ToHashSet(StringComparer.Ordinal);
        box.Loaded += (_, _) =>
        {
            foreach (var item in box.Items.Cast<object>().Where(item => selectedValues.Contains(item?.ToString() ?? "")))
                box.SelectedItems.Add(item);
        };
        inputs.Add(id, box); fields.Children.Add(box); return this;
    }
    public FieldsDialog TokenBalance(string id, string label, int totalTokens, int responseTokens, int fixedReservedTokens = 0, bool editable = true, string status = "Custom agent default")
    {
        var heading = Ui.Text(label, 13); labels.Add(id, heading); fields.Children.Add(heading);
        var balance = new TokenBalanceControl();
        balance.Configure(totalTokens, responseTokens, fixedReservedTokens, editable, status);
        inputs.Add(id, balance); fields.Children.Add(balance); return this;
    }
    public string Selection(string id) => ((ComboBox)inputs[id]).SelectedItem as string ?? throw new ArgumentException("Select an option.");
    public List<string> Selections(string id) => ((ListBox)inputs[id]).SelectedItems.Cast<object>()
        .Select(item => item?.ToString() ?? "").Where(item => item.Length > 0).ToList();
    public FieldsDialog Secret(string id, string label, string value)
    {
        var heading = Ui.Text(label, 13); labels.Add(id, heading); fields.Children.Add(heading);
        var box = new PasswordBox { Password = value, Margin = new Thickness(0, 0, 12, 13) };
        inputs.Add(id, box); fields.Children.Add(box); return this;
    }
    public FieldsDialog ActionButton(string label, Action<FieldsDialog> action)
    {
        var button = Ui.Button(label, () => action(this)); button.HorizontalAlignment = HorizontalAlignment.Left; button.Margin = new Thickness(0, 0, 0, 13);
        fields.Children.Add(button); return this;
    }
    public void SetValue(string id, string value) => ((TextBox)inputs[id]).Text = value;
    public FieldsDialog SetEnabled(string id, bool enabled) { inputs[id].IsEnabled = enabled; return this; }
    public FieldsDialog SetReadOnly(string id, bool readOnly = true) { ((TextBox)inputs[id]).IsReadOnly = readOnly; return this; }
    public FieldsDialog SetVisible(string id, bool visible)
    {
        var visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        inputs[id].Visibility = visibility;
        if (labels.TryGetValue(id, out var label)) label.Visibility = visibility;
        return this;
    }
    public FieldsDialog SetLabel(string id, string text)
    {
        if (!labels.TryGetValue(id, out var label) || label is not TextBlock block) throw new ArgumentException("The field has no separate label.", nameof(id));
        block.Text = text; return this;
    }
    public FieldsDialog SetSelection(string id, string value)
    {
        var choice = (ComboBox)inputs[id];
        if (!choice.Items.Contains(value)) throw new ArgumentException("The choice is unavailable.", nameof(value));
        choice.SelectedItem = value; return this;
    }
    public FieldsDialog SetTokenBalance(string id, int totalTokens, int responseTokens, int fixedReservedTokens, bool editable, string status)
    {
        ((TokenBalanceControl)inputs[id]).Configure(totalTokens, responseTokens, fixedReservedTokens, editable, status);
        return this;
    }
    public FieldsDialog WhenTokenBalanceChanged(string id, Action<FieldsDialog, int> action)
    {
        var balance = (TokenBalanceControl)inputs[id];
        balance.ResponseTokensChanged += (_, _) => action(this, balance.ResponseTokens);
        return this;
    }
    public FieldsDialog WhenTextChanged(string id, Action<FieldsDialog> action)
    {
        ((TextBox)inputs[id]).TextChanged += (_, _) => action(this);
        return this;
    }
    public FieldsDialog WhenChoiceChanged(string id, Action<FieldsDialog> action)
    {
        ((ComboBox)inputs[id]).SelectionChanged += (_, _) => action(this);
        action(this); return this;
    }
    public FieldsDialog WhenAnyInputChanged(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        foreach (var input in inputs.Values)
        {
            switch (input)
            {
                case TextBox box: box.TextChanged += (_, _) => action(); break;
                case ComboBox choice: choice.SelectionChanged += (_, _) => action(); break;
                case CheckBox check:
                    check.Checked += (_, _) => action(); check.Unchecked += (_, _) => action(); break;
                case ListBox list: list.SelectionChanged += (_, _) => action(); break;
                case PasswordBox secret: secret.PasswordChanged += (_, _) => action(); break;
            }
        }
        return this;
    }
    public FieldsDialog ConfigureSubmitAsync(string label, Func<Task<bool>> action) => ConfigureSubmitAsync(label, action, saveOnClose: true);
    public FieldsDialog ConfigureSubmitAsync(string label, Func<Task<bool>> action, bool saveOnClose)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(label); ArgumentNullException.ThrowIfNull(action);
        submitButton.Content = label; closeButton.Content = "Discard unsaved draft"; submitAsync = action; submitOnClose = saveOnClose; return this;
    }
    public FieldsDialog SetInlineStatus(string message, bool error = false)
    {
        inlineStatus.Text = message;
        inlineStatus.Foreground = error ? Ui.ErrorText : Ui.Muted;
        inlineStatus.Visibility = string.IsNullOrWhiteSpace(message) ? Visibility.Collapsed : Visibility.Visible;
        return this;
    }
    public string InlineStatusText => inlineStatus.Text;
    public bool InlineStatusIsError => inlineStatus.Foreground == Ui.ErrorText;
    public void DiscardDraftAndClose() { closeApproved = true; Close(); }
    public string SecretValue(string id) => ((PasswordBox)inputs[id]).Password;
    public string Value(string id) => ((TextBox)inputs[id]).Text.Trim();
    public string Raw(string id) => ((TextBox)inputs[id]).Text;
    public bool Checked(string id) => ((CheckBox)inputs[id]).IsChecked == true;
    public int Number(string id, int min, int max)
    {
        if (!int.TryParse(Value(id), out var value) || value < min || value > max) throw new ArgumentException($"{id} must be between {min} and {max}.");
        return value;
    }
    public List<string> Lines(string id) => Raw(id).Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
}
