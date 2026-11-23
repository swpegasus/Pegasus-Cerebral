using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using Cerebral.Core;
using Cerebral.Desktop;
using Cerebral.Desktop.Plugins;

namespace Cerebral.Desktop.Tests;

internal static class AppearanceThemeTests
{
    public static void Run(Application app)
    {
        var checks = 0;
        void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
            checks++;
        }

        Check(DesktopThemeCatalog.Resolve(AppearanceThemePolicy.System, () => true).Id == AppearanceThemePolicy.Light &&
              DesktopThemeCatalog.Resolve(AppearanceThemePolicy.System, () => false).Id == AppearanceThemePolicy.Dark,
            "System appearance must deterministically resolve through the Windows-theme boundary.");
        Check(DesktopThemeCatalog.Resolve(AppearanceThemePolicy.Light, () => false).Id == AppearanceThemePolicy.Light &&
              DesktopThemeCatalog.Resolve(AppearanceThemePolicy.Dark, () => true).Id == AppearanceThemePolicy.Dark,
            "Explicit Light and Dark choices must not follow later system changes.");

        Ui.ApplyTheme(app);
        var button = Ui.Button("Theme fixture", () => { });
        var primaryButton = Ui.Button("Primary theme fixture", () => { }, true);
        var disabledButton = Ui.Button("Disabled fixture", () => { }, true); disabledButton.IsEnabled = false;
        var combo = new ComboBox { Items = { "Windows default", "Microphone" }, SelectedIndex = 0 };
        combo.Style = (Style)app.Resources[typeof(ComboBox)];
        var comboItem = new ComboBoxItem { Content = "Microphone", IsSelected = true,
            Style = (Style)app.Resources[typeof(ComboBoxItem)] };
        var selectedTabText = new TextBlock { Text = "Selected" };
        var selectedPage = new TextBlock { Text = "Selected page" };
        var selectedTab = Ui.TabPage("Selected", selectedPage); selectedTab.Header = selectedTabText; selectedTab.IsSelected = true;
        var disabledTab = new TabItem { Header = "Disabled", IsEnabled = false };
        var extendedListStyle = Ui.ExtendStyle<ListBoxItem>();
        var host = new Window { Content = new TabControl { Items = { selectedTab, disabledTab } } };
        var pluginText = new TextBlock { Text = "Plain plugin text" };
        var connectedHost = new ThemeHost { Content = new Grid { Children = { pluginText } } };
        var initialTheme = Ui.ResolvedTheme == AppearanceThemePolicy.Dark ? DesktopThemeCatalog.Dark : DesktopThemeCatalog.Light;
        Check(button.Background is SolidColorBrush buttonSurface && buttonSurface.Color == initialTheme.RaisedSurface &&
               app.Resources[SystemColors.HighlightBrushKey] is SolidColorBrush selection && selection.Color == initialTheme.Selection,
            "Shared controls and Windows selection resources must resolve the current theme palette.");
        var textStyle = (Style)app.Resources[typeof(TextBlock)];
        Check(textStyle.Setters.OfType<Setter>().All(setter => setter.Property != TextBlock.ForegroundProperty) &&
               ReferenceEquals(app.Resources[DesktopThemeResourceKeys.ErrorTextBrush], Ui.ErrorText),
            "Native plugin text must inherit its owning control state while documented theme resource keys remain available.");
        Check(ReferenceEquals(extendedListStyle.BasedOn, app.Resources[typeof(ListBoxItem)]),
            "Local layout and automation styles must extend the shared control style instead of replacing its theme-state contract.");
        var primaryHover = primaryButton.Style.Triggers.OfType<Trigger>().Single(trigger =>
            trigger.Property == UIElement.IsMouseOverProperty && Equals(trigger.Value, true));
        var primaryHoverBackground = (SolidColorBrush)primaryHover.Setters.OfType<Setter>()
            .Single(setter => setter.Property == Control.BackgroundProperty).Value;
        var primaryHoverForeground = (SolidColorBrush)primaryHover.Setters.OfType<Setter>()
            .Single(setter => setter.Property == Control.ForegroundProperty).Value;
        Check(ReferenceEquals(primaryHoverBackground, Ui.Selection) && ReferenceEquals(primaryHoverForeground, Ui.SelectionText),
            "Primary buttons must replace the inherited ordinary hover colors with a paired readable surface and foreground.");

        Ui.ApplyAppearanceTheme(AppearanceThemePolicy.Light);
        Check(Contrast(primaryHoverBackground.Color, primaryHoverForeground.Color) >= 4.5,
            "Primary-button hover text must meet normal-text contrast in Light.");
        CheckListControls(app, Check);
        CheckComboControls(combo, Check);
        CheckCheckBoxControls(app, Check);

        Ui.ApplyAppearanceTheme(AppearanceThemePolicy.Dark);
        Check(connectedHost.Foreground is SolidColorBrush inheritedHostInk && inheritedHostInk.Color == DesktopThemeCatalog.Dark.Ink &&
               pluginText.Foreground is SolidColorBrush inheritedPluginInk && inheritedPluginInk.Color == DesktopThemeCatalog.Dark.Ink,
            "Plain text in a plugin must inherit live theme ink from the derived application window without local TextBlock overrides.");
        Check(Ui.AppearanceTheme == AppearanceThemePolicy.Dark && Ui.ResolvedTheme == AppearanceThemePolicy.Dark &&
               button.Background is SolidColorBrush darkButton && darkButton.Color == DesktopThemeCatalog.Dark.RaisedSurface &&
               Ui.Surface.Color == DesktopThemeCatalog.Dark.Surface,
            "An existing control must adopt Dark without rebuilding the window.");
        selectedTab.ApplyTemplate();
        var headerPresenter = Descendants(selectedTab).OfType<ContentPresenter>().Single();
        headerPresenter.ApplyTemplate();
        Check(ReferenceEquals(headerPresenter.Content, selectedTabText) && ReferenceEquals(selectedTab.Content, selectedPage),
            "The shared TabItem template must render the header in the strip and leave the selected page owned by the TabControl.");
        Check(selectedTab.HorizontalContentAlignment == HorizontalAlignment.Stretch &&
              selectedTab.VerticalContentAlignment == VerticalAlignment.Stretch &&
              headerPresenter.HorizontalAlignment == HorizontalAlignment.Center &&
              headerPresenter.VerticalAlignment == VerticalAlignment.Center,
            "Tab labels must remain centered while selected pages stretch from the top of their host.");
        Check(selectedTab.Foreground is SolidColorBrush selectedForeground && selectedForeground.Color == DesktopThemeCatalog.Dark.SelectionText &&
              selectedTabText.Foreground is SolidColorBrush inheritedForeground && inheritedForeground.Color == DesktopThemeCatalog.Dark.SelectionText,
            "Selected tab headers must retain readable selection ink.");
        Check(selectedPage.Foreground is SolidColorBrush pageForeground && pageForeground.Color == DesktopThemeCatalog.Dark.Ink,
            "Ordinary text on a selected tab page must retain readable theme ink instead of inheriting selected-header ink.");
        var replacementPage = new Grid(); var replacementText = new TextBlock { Text = "Replacement plugin page" }; replacementPage.Children.Add(replacementText);
        Ui.SetTabPageContent(selectedTab, replacementPage);
        Check(replacementText.Foreground is SolidColorBrush replacementInk && replacementInk.Color == DesktopThemeCatalog.Dark.Ink,
            "Replacing a page while its tab stays selected must reestablish the shared text inheritance boundary.");
        var explicitPage = new TextBlock { Text = "Intentional custom color", Foreground = Brushes.Coral };
        Ui.SetTabPageContent(selectedTab, explicitPage);
        Check(ReferenceEquals(explicitPage.Foreground, Brushes.Coral), "The page boundary must preserve explicit plugin colors.");
        var pluginTextStyle = new Style(typeof(TextBlock)); pluginTextStyle.Setters.Add(new Setter(TextBlock.ForegroundProperty, Brushes.Coral));
        var styledPage = new TextBlock { Text = "Intentional styled color", Style = pluginTextStyle };
        Ui.SetTabPageContent(selectedTab, styledPage);
        Check(ReferenceEquals(styledPage.Foreground, Brushes.Coral), "The page boundary must preserve colors supplied by a plugin's own style.");
        Ui.SetTabPageContent(selectedTab, selectedPage);
        Check(button.BorderBrush is SolidColorBrush buttonBorder && buttonBorder.Color == DesktopThemeCatalog.Dark.Muted &&
               button.Opacity == 1d,
            "Enabled buttons must use a visible theme border without losing their normal opacity.");
        Check(Contrast(primaryHoverBackground.Color, primaryHoverForeground.Color) >= 4.5,
            "Primary-button hover text must meet normal-text contrast in Dark.");
        disabledButton.ApplyTemplate(); disabledTab.ApplyTemplate();
        Check(disabledButton.Background is SolidColorBrush disabledButtonBackground && disabledButtonBackground.Color == DesktopThemeCatalog.Dark.Surface &&
              disabledButton.Foreground is SolidColorBrush disabledButtonText && disabledButtonText.Color == DesktopThemeCatalog.Dark.Muted &&
              disabledButton.Opacity == 1d && Luminance(disabledButtonText.Color) - Luminance(disabledButtonBackground.Color) > .45,
            "Disabled buttons must retain explicit readable text and surfaces in Dark instead of inheriting Windows gray-on-gray rendering.");
        Check(disabledTab.Background is SolidColorBrush disabledTabBackground && disabledTabBackground.Color == DesktopThemeCatalog.Dark.Surface &&
              disabledTab.Foreground is SolidColorBrush disabledTabText && disabledTabText.Color == DesktopThemeCatalog.Dark.Muted &&
              disabledTab.Opacity == 1d &&
              ((Style)app.Resources[typeof(TabItem)]).Triggers.OfType<MultiTrigger>().Any(trigger =>
                  trigger.Conditions.Any(condition => condition.Property == UIElement.IsMouseOverProperty && Equals(condition.Value, true)) &&
                  trigger.Conditions.Any(condition => condition.Property == System.Windows.Controls.Primitives.Selector.IsSelectedProperty && Equals(condition.Value, false))),
            "Disabled tabs must remain readable, and hover visuals must apply only to inactive tabs so the selected page cannot flash its header.");
        Check(combo.Background is SolidColorBrush comboBackground && comboBackground.Color == DesktopThemeCatalog.Dark.Surface &&
              combo.Foreground is SolidColorBrush comboForeground && comboForeground.Color == DesktopThemeCatalog.Dark.Ink &&
              Contrast(comboBackground.Color, comboForeground.Color) >= 4.5 &&
              comboItem.Background is SolidColorBrush itemBackground && itemBackground.Color == DesktopThemeCatalog.Dark.Accent &&
              comboItem.Foreground is SolidColorBrush itemForeground && itemForeground.Color == DesktopThemeCatalog.Dark.OnAccent &&
              Contrast(itemBackground.Color, itemForeground.Color) >= 4.5,
            "The owned ComboBox field and selected dropdown item must retain readable paired theme colors.");
        Check(Luminance(Ui.Selection.Color) > .9 && Luminance(Ui.SelectionText.Color) < .15,
            "Dark-mode selection must use a light background with dark selected text.");
        Check(Luminance(Ui.ErrorText.Color) - Luminance(Ui.ErrorBackground.Color) > .65,
            "Dark-mode error panels must use high-contrast light text on a dark red background.");


        foreach (var palette in new[] { DesktopThemeCatalog.Light, DesktopThemeCatalog.Dark })
        {
            Check(palette.Accent.B > palette.Accent.G && palette.Accent.B > palette.Accent.R,
                palette.Id + " shared accents must use the approved blue brand family.");
            foreach (var surface in new[] { palette.Window, palette.Surface, palette.RaisedSurface,
                palette.Hover, palette.Pressed, palette.CodeSurface })
                Check(Contrast(palette.Ink, surface) >= 4.5,
                    palette.Id + " normal and pressed text must remain legible on every ordinary shared surface.");
            Check(Contrast(palette.Muted, palette.Surface) >= 4.5 && Contrast(palette.Muted, palette.RaisedSurface) >= 4.5,
                palette.Id + " secondary and disabled text must remain readable without opacity tricks.");
            Check(Contrast(palette.Accent, palette.OnAccent) >= 4.5 &&
                  Contrast(palette.Selection, palette.SelectionText) >= 4.5,
                palette.Id + " accent and selected controls must retain paired foreground/background contrast.");
        }
        var pressedTrigger = primaryButton.Style.Triggers.OfType<Trigger>().Single(trigger =>
            trigger.Property == System.Windows.Controls.Primitives.ButtonBase.IsPressedProperty && Equals(trigger.Value, true));
        Check(ReferenceEquals(pressedTrigger.Setters.OfType<Setter>().Single(setter =>
                  setter.Property == Control.ForegroundProperty).Value, Ui.Ink),
            "Primary pressed controls use the matching normal ink for the pressed surface in both palettes.");
        foreach (var preference in new[] { AppearanceThemePolicy.Light, AppearanceThemePolicy.Dark })
        {
            Ui.ApplyAppearanceTheme(preference);
            var field = new TextBox { Text = "Disabled but readable", IsEnabled = false };
            var disabledCombo = new ComboBox { IsEnabled = false, Items = { "Selected value" }, SelectedIndex = 0 };
            var sample = new StackPanel { Children = { field, disabledCombo } };
            sample.Measure(new Size(320, 120)); sample.Arrange(new Rect(0, 0, 320, 120)); sample.UpdateLayout();
            Check(field.Opacity == 1 && disabledCombo.Opacity == 1 &&
                  field.Foreground is SolidColorBrush fieldInk && disabledCombo.Foreground is SolidColorBrush comboInk &&
                  field.Background is SolidColorBrush fieldSurface && disabledCombo.Background is SolidColorBrush comboSurface &&
                  Contrast(fieldInk.Color, fieldSurface.Color) >= 4.5 && Contrast(comboInk.Color, comboSurface.Color) >= 4.5,
                preference + " shared disabled editors keep explicit readable colors at full opacity.");
        }
        Ui.ApplyAppearanceTheme(AppearanceThemePolicy.Dark);

        var tabControl = new TabControl { Items = { Ui.TabPage("Fixture", new TextBlock { Text = "Page" }) } };
        tabControl.Measure(new Size(300, 180)); tabControl.Arrange(new Rect(0, 0, 300, 180)); tabControl.ApplyTemplate();
        Check(tabControl.Background is SolidColorBrush pageSurface && pageSurface.Color == Ui.Surface.Color &&
              tabControl.Foreground is SolidColorBrush pageInk && pageInk.Color == Ui.Ink.Color,
            $"A bare plugin TabControl must receive the shared page surface and ink, including Dark mode. Background={tabControl.Background}, Foreground={tabControl.Foreground}, BackgroundSource={DependencyPropertyHelper.GetValueSource(tabControl, Control.BackgroundProperty).BaseValueSource}, ForegroundSource={DependencyPropertyHelper.GetValueSource(tabControl, Control.ForegroundProperty).BaseValueSource}, Styled={ReferenceEquals(tabControl.Style, app.Resources[typeof(TabControl)])}");
        Ui.ApplyAppearanceTheme(AppearanceThemePolicy.Light);
        Check(((SolidColorBrush)tabControl.Background).Color == DesktopThemeCatalog.Light.Surface &&
              ((SolidColorBrush)tabControl.Foreground).Color == DesktopThemeCatalog.Light.Ink,
            "Implicit TabControl style brushes must update live even when WPF clones a Freezable setter value.");
        Ui.ApplyAppearanceTheme(AppearanceThemePolicy.Dark);
        var textField = Ui.Input("Editor fixture", true); textField.Style = (Style)app.Resources[typeof(TextBox)];
        textField.Measure(new Size(300, 110)); textField.Arrange(new Rect(0, 0, 300, 110)); textField.ApplyTemplate();
        Check(textField.Template.FindName("PART_ContentHost", textField) is ScrollViewer &&
              textField.Template.FindName("TextFieldBorder", textField) is Border border && border.CornerRadius.TopLeft == 5 &&
              textField.SelectionBrush is SolidColorBrush selectedSurface && selectedSurface.Color == Ui.Selection.Color &&
              textField.SelectionTextBrush is SolidColorBrush selectedInk && selectedInk.Color == Ui.SelectionText.Color &&
              textField.SelectionOpacity == 1,
            "Shared text fields must retain the WPF editor host and explicit selection contrast without platform disabled dimming.");
        textField.Select(0, 6); textField.SelectedText = "Native";
        Check(textField.Text == "Native fixture" && textField.AcceptsReturn &&
              textField.VerticalScrollBarVisibility == ScrollBarVisibility.Auto,
            "The shared chrome must preserve normal selection replacement and multiline scrolling.");
        var passwordField = new PasswordBox { Password = "fixture", Style = (Style)app.Resources[typeof(PasswordBox)] }; passwordField.Measure(new Size(300, 40)); passwordField.Arrange(new Rect(0, 0, 300, 40)); passwordField.ApplyTemplate();
        Check(passwordField.Template.FindName("PART_ContentHost", passwordField) is ScrollViewer && passwordField.Password == "fixture",
            "The password editor must retain its own native content host and value semantics.");

        Ui.ApplyAppearanceTheme(AppearanceThemePolicy.System, () => false);
        Check(Ui.AppearanceTheme == AppearanceThemePolicy.System && Ui.ResolvedTheme == AppearanceThemePolicy.Dark,
            "The saved System choice must remain distinct from its current resolved Dark palette.");

        var futureSkin = DesktopThemeCatalog.Light with { Id = "future-skin", Accent = Color.FromRgb(90, 60, 130) };
        Ui.ApplyThemeDefinition(futureSkin);
        Check(Ui.ResolvedTheme == "future-skin" && Ui.Accent.Color == futureSkin.Accent,
            "The renderer must accept a complete named definition without changing the core preference transport.");

        var rejected = false;
        try { Ui.ApplyAppearanceTheme("future-skin"); }
        catch (ArgumentException) { rejected = true; }
        Check(rejected, "Unregistered skin IDs must remain outside the current core setting contract.");

        Ui.ApplyAppearanceTheme(AppearanceThemePolicy.System, () => true);
        Check(selectedPage.Foreground is SolidColorBrush lightPageInk && lightPageInk.Color == DesktopThemeCatalog.Light.Ink,
            "The page inheritance boundary must follow later palette changes without replacing the page.");
        host.Close();
        var portableFileChecks = AppearancePortableThemeFileTests.RunAsync().GetAwaiter().GetResult();
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            success = true,
            appearanceThemeChecks = checks,
            portableFileChecks,
            evidence = "deterministic palette resolution, readable primary-button, checkbox and list states, native three-state checkbox automation, full-row checkbox hit surface, native list/GridView content and multiple selection, live mutable WPF resources, dark selection contrast and future definition boundary",
            windowsRegistryRead = false,
            persistentPreferencesWritten = false
        }));
    }

    private static void CheckCheckBoxControls(Application app, Action<bool, string> check)
    {
        TextBlock Label(string text) { var label = Ui.Text(text); label.Margin = new Thickness(0); return label; }
        var uncheckedText = Label("Unchecked choice");
        var checkedText = Label("Checked choice");
        var indeterminateText = Label("Indeterminate choice");
        var disabledText = Label("Disabled choice");
        var sharedStyle = (Style)app.Resources[typeof(CheckBox)];
        var uncheckedBox = new CheckBox { Content = uncheckedText, Style = sharedStyle };
        var checkedBox = new CheckBox { Content = checkedText, IsChecked = true, Style = sharedStyle };
        var indeterminateBox = new CheckBox { Content = indeterminateText, IsThreeState = true, IsChecked = null, Style = sharedStyle };
        var disabledBox = new CheckBox { Content = disabledText, IsChecked = true, IsEnabled = false, Style = sharedStyle };
        var automationBox = new CheckBox { Content = "Automation choice", IsThreeState = true, Style = sharedStyle };
        var body = new StackPanel { Width = 320, Children = { uncheckedBox, checkedBox, indeterminateBox, disabledBox, automationBox } };
        body.Measure(new Size(320, 240)); body.Arrange(new Rect(0, 0, 320, 240)); body.UpdateLayout();
        foreach (var box in new[] { uncheckedBox, checkedBox, indeterminateBox, disabledBox, automationBox }) box.ApplyTemplate();

        var hover = sharedStyle.Triggers.OfType<Trigger>().Single(trigger =>
            trigger.Property == UIElement.IsMouseOverProperty && Equals(trigger.Value, true));
        var hoverSurface = (SolidColorBrush)hover.Setters.OfType<Setter>()
            .Single(setter => setter.Property == Control.BackgroundProperty).Value;
        var hoverInk = (SolidColorBrush)hover.Setters.OfType<Setter>()
            .Single(setter => setter.Property == Control.ForegroundProperty).Value;
        var pressed = sharedStyle.Triggers.OfType<Trigger>().Single(trigger =>
            trigger.Property == System.Windows.Controls.Primitives.ButtonBase.IsPressedProperty && Equals(trigger.Value, true));
        var pressedSurface = (SolidColorBrush)pressed.Setters.OfType<Setter>()
            .Single(setter => setter.Property == Control.BackgroundProperty).Value;
        var pressedInk = (SolidColorBrush)pressed.Setters.OfType<Setter>()
            .Single(setter => setter.Property == Control.ForegroundProperty).Value;
        var focus = sharedStyle.Triggers.OfType<Trigger>().Single(trigger =>
            trigger.Property == UIElement.IsKeyboardFocusWithinProperty && Equals(trigger.Value, true));
        var focusBorder = (SolidColorBrush)focus.Setters.OfType<Setter>()
            .Single(setter => setter.Property == Control.BorderBrushProperty).Value;
        check(ReferenceEquals(focusBorder, Ui.Accent),
            "The shared CheckBox style must expose an explicit focus border without replacing native focus or toggle behavior.");

        foreach (var theme in new[] { DesktopThemeCatalog.Light, DesktopThemeCatalog.Dark })
        {
            Ui.ApplyThemeDefinition(theme); body.UpdateLayout();
            var uncheckedChrome = (Border)uncheckedBox.Template.FindName("CheckBoxChrome", uncheckedBox);
            var uncheckedIndicator = (Border)uncheckedBox.Template.FindName("CheckBoxIndicator", uncheckedBox);
            var checkedIndicator = (Border)checkedBox.Template.FindName("CheckBoxIndicator", checkedBox);
            var checkedMark = (System.Windows.Shapes.Path)checkedBox.Template.FindName("CheckBoxCheckMark", checkedBox);
            var indeterminateIndicator = (Border)indeterminateBox.Template.FindName("CheckBoxIndicator", indeterminateBox);
            var indeterminateMark = (System.Windows.Shapes.Rectangle)indeterminateBox.Template.FindName("CheckBoxIndeterminateMark", indeterminateBox);
            var disabledIndicator = (Border)disabledBox.Template.FindName("CheckBoxIndicator", disabledBox);
            var disabledMark = (System.Windows.Shapes.Path)disabledBox.Template.FindName("CheckBoxCheckMark", disabledBox);

            check(uncheckedBox.Background is SolidColorBrush uncheckedSurface && uncheckedSurface.Color == theme.Surface &&
                  uncheckedBox.Foreground is SolidColorBrush uncheckedInk && uncheckedInk.Color == theme.Ink &&
                  uncheckedText.Foreground is SolidColorBrush inheritedUncheckedInk && inheritedUncheckedInk.Color == theme.Ink &&
                  Contrast(uncheckedSurface.Color, uncheckedInk.Color) >= 4.5 &&
                  uncheckedIndicator.Background is SolidColorBrush indicatorSurface && indicatorSurface.Color == theme.Surface &&
                  uncheckedIndicator.BorderBrush is SolidColorBrush indicatorBorder && indicatorBorder.Color == theme.Muted &&
                  Contrast(indicatorSurface.Color, indicatorBorder.Color) >= 3,
                theme.Id + " unchecked CheckBox must paint readable inherited content and an explicit indicator.");
            check(checkedBox.Background is SolidColorBrush checkedSurface && checkedSurface.Color == theme.Surface &&
                  checkedBox.Foreground is SolidColorBrush checkedInk && checkedInk.Color == theme.Ink &&
                  checkedText.Foreground is SolidColorBrush inheritedCheckedInk && inheritedCheckedInk.Color == theme.Ink &&
                  Contrast(checkedSurface.Color, checkedInk.Color) >= 4.5 &&
                  checkedIndicator.Background is SolidColorBrush checkedIndicatorSurface && checkedIndicatorSurface.Color == theme.Accent &&
                  checkedMark.Stroke is SolidColorBrush checkedMarkInk && checkedMarkInk.Color == theme.OnAccent &&
                  Contrast(checkedIndicatorSurface.Color, checkedMarkInk.Color) >= 4.5 && checkedMark.Visibility == Visibility.Visible,
                theme.Id + " checked CheckBox must retain readable row content while showing a paired check indicator.");
            check(indeterminateBox.Background is SolidColorBrush indeterminateSurface && indeterminateSurface.Color == theme.Surface &&
                  indeterminateBox.Foreground is SolidColorBrush indeterminateInk && indeterminateInk.Color == theme.Ink &&
                  indeterminateText.Foreground is SolidColorBrush inheritedIndeterminateInk && inheritedIndeterminateInk.Color == theme.Ink &&
                  Contrast(indeterminateSurface.Color, indeterminateInk.Color) >= 4.5 &&
                  indeterminateIndicator.Background is SolidColorBrush indeterminateIndicatorSurface && indeterminateIndicatorSurface.Color == theme.Selection &&
                  indeterminateMark.Fill is SolidColorBrush indeterminateMarkInk && indeterminateMarkInk.Color == theme.SelectionText &&
                  Contrast(indeterminateIndicatorSurface.Color, indeterminateMarkInk.Color) >= 4.5 && indeterminateMark.Visibility == Visibility.Visible,
                theme.Id + " indeterminate CheckBox must remain distinct and readable.");
            check(disabledBox.Background is SolidColorBrush disabledSurface && disabledSurface.Color == theme.Surface &&
                  disabledBox.Foreground is SolidColorBrush disabledInk && disabledInk.Color == theme.Muted &&
                  disabledText.Foreground is SolidColorBrush inheritedDisabledInk && inheritedDisabledInk.Color == theme.Muted &&
                  Contrast(disabledSurface.Color, disabledInk.Color) >= 4.5 && disabledBox.Opacity == 1d &&
                  disabledIndicator.Background is SolidColorBrush disabledIndicatorSurface && disabledIndicatorSurface.Color == theme.Surface &&
                  disabledIndicator.BorderBrush is SolidColorBrush disabledIndicatorBorder && disabledIndicatorBorder.Color == theme.Muted &&
                  disabledMark.Stroke is SolidColorBrush disabledMarkInk && disabledMarkInk.Color == theme.Muted,
                theme.Id + " disabled CheckBox must retain its state glyph and readable muted content without opacity loss.");
            check(hoverSurface.Color == theme.Hover && hoverInk.Color == theme.Ink && Contrast(hoverSurface.Color, hoverInk.Color) >= 4.5,
                theme.Id + " CheckBox hover must use a readable paired surface and foreground.");
            check(pressedSurface.Color == theme.Pressed && pressedInk.Color == theme.Ink && Contrast(pressedSurface.Color, pressedInk.Color) >= 4.5,
                theme.Id + " CheckBox pressed state must remain readable while native input owns the toggle.");
            check(focusBorder.Color == theme.Accent && Contrast(theme.Surface, focusBorder.Color) >= 3,
                theme.Id + " CheckBox focus outline must remain visible against its row surface.");
            var hit = VisualTreeHelper.HitTest(uncheckedBox,
                new Point(uncheckedBox.ActualWidth - 2, uncheckedBox.ActualHeight / 2))?.VisualHit;
            check(uncheckedChrome.Background is SolidColorBrush hitSurface && hitSurface.Color == theme.Surface,
                theme.Id + " CheckBox template must paint the row instead of leaving a text-only hit target.");
            check(Math.Abs(uncheckedChrome.ActualWidth - uncheckedBox.ActualWidth) < .5,
                theme.Id + " CheckBox chrome must span the complete arranged row.");
            check(hit is not null,
                theme.Id + " CheckBox must expose a native hit target at the far edge of its painted row.");
        }

        var presenter = (ContentPresenter)checkedBox.Template.FindName("CheckBoxContent", checkedBox);
        check(ReferenceEquals(presenter.Content, checkedText) &&
              DependencyPropertyHelper.GetValueSource(checkedText, System.Windows.Documents.TextElement.ForegroundProperty).BaseValueSource == BaseValueSource.Inherited,
            "The shared CheckBox template must preserve arbitrary content and let ordinary Ui.Text labels inherit the owning control state.");
        var peer = new CheckBoxAutomationPeer(automationBox);
        var toggle = peer.GetPattern(PatternInterface.Toggle) as IToggleProvider;
        check(toggle is not null && peer.GetName() == "Automation choice" && toggle.ToggleState == ToggleState.Off,
            "A themed CheckBox must retain its native accessible name and Toggle provider.");
        toggle!.Toggle();
        check(toggle.ToggleState == ToggleState.On && automationBox.IsChecked == true,
            "Native automation must toggle a themed CheckBox on.");
        toggle.Toggle();
        check(toggle.ToggleState == ToggleState.Indeterminate && automationBox.IsChecked is null,
            "Native automation must preserve the three-state indeterminate transition.");
        toggle.Toggle();
        check(toggle.ToggleState == ToggleState.Off && automationBox.IsChecked == false,
            "Native automation must complete the native three-state cycle.");
    }

    private static void CheckComboControls(ComboBox combo, Action<bool, string> check)
    {
        var body = new Grid { Children = { combo } };
        try
        {
            foreach (var theme in new[] { DesktopThemeCatalog.Light, DesktopThemeCatalog.Dark })
            {
                Ui.ApplyThemeDefinition(theme);
                body.Measure(new Size(320, 40)); body.Arrange(new Rect(0, 0, 320, 40)); body.UpdateLayout();
                var selection = combo.Template.FindName("SelectionPresenter", combo) as ContentPresenter;
                var toggle = combo.Template.FindName("DropDownToggle", combo) as System.Windows.Controls.Primitives.ToggleButton;
                toggle?.ApplyTemplate(); selection?.ApplyTemplate();
                var border = toggle is null ? null : VisualTreeHelper.GetChild(toggle, 0) as Border;
                var selectedText = selection is null ? null : Descendants(selection).OfType<TextBlock>().SingleOrDefault();
                check(selection?.Content is string text && text == "Windows default" &&
                      border?.Background is SolidColorBrush surface && surface.Color == theme.Surface &&
                      selectedText?.Foreground is SolidColorBrush ink && ink.Color == theme.Ink && Contrast(surface.Color, ink.Color) >= 4.5,
                    theme.Id + " closed ComboBox must paint its actual selected text with readable ink on the owned field surface.");
                combo.IsEditable = true; body.UpdateLayout();
                check(selection?.Visibility == Visibility.Collapsed &&
                      combo.Template.FindName("PART_EditableTextBox", combo) is TextBox { Visibility: Visibility.Visible } editor &&
                      editor.Background is SolidColorBrush editorSurface && editorSurface.Color == theme.Surface &&
                      editor.Foreground is SolidColorBrush editorInk && editorInk.Color == theme.Ink && Contrast(editorSurface.Color, editorInk.Color) >= 4.5 &&
                      combo.Template.FindName("PART_Popup", combo) is System.Windows.Controls.Primitives.Popup,
                    theme.Id + " ComboBox template must preserve a readable editable field and native popup contract.");
                combo.IsEditable = false; body.UpdateLayout();
            }
        }
        finally { body.Children.Clear(); }
    }

    private static void CheckListControls(Application app, Action<bool, string> check)
    {
        var list = new ListBox { Height = 110, SelectionMode = SelectionMode.Multiple };
        for (var index = 0; index < 30; index++) list.Items.Add("Local agent " + index);
        list.SelectedItems.Add(list.Items[0]); list.SelectedItems.Add(list.Items[2]);
        var plain = new ListView { Height = 80, Items = { "Plain list entry" }, SelectedIndex = 0 };
        var columns = new GridView();
        columns.Columns.Add(new GridViewColumn { Header = "Agent", Width = 160, DisplayMemberBinding = new Binding(nameof(ListRow.Name)) });
        columns.Columns.Add(new GridViewColumn { Header = "Uses", Width = 80, DisplayMemberBinding = new Binding(nameof(ListRow.Uses)) });
        var table = new ListView { Height = 80, View = columns, Items = { new ListRow("Fixture agent", 7) }, SelectedIndex = 0 };
        var tree = new TreeView { Height = 50, Items = { new TreeViewItem { Header = "Project agents" } } };
        var body = new StackPanel { Children = { list, plain, table, tree } };
        var window = new Window { Content = body };
        try
        {
            body.Measure(new Size(420, 400)); body.Arrange(new Rect(0, 0, 420, 400)); body.UpdateLayout();
            var normal = (ListBoxItem)list.ItemContainerGenerator.ContainerFromIndex(1);
            var selected = (ListBoxItem)list.ItemContainerGenerator.ContainerFromIndex(0);
            var ordinaryViewItem = (ListViewItem)plain.ItemContainerGenerator.ContainerFromIndex(0);
            var tableItem = (ListViewItem)table.ItemContainerGenerator.ContainerFromIndex(0);
            foreach (var item in new[] { normal, selected, ordinaryViewItem, tableItem }) item.ApplyTemplate();

            foreach (var theme in new[] { DesktopThemeCatalog.Light, DesktopThemeCatalog.Dark })
            {
                Ui.ApplyThemeDefinition(theme);
                body.UpdateLayout();
                foreach (var control in new Control[] { list, plain, table, tree })
                    check(control.Background is SolidColorBrush surface && surface.Color == theme.Surface &&
                          control.Foreground is SolidColorBrush ink && ink.Color == theme.Ink && Contrast(surface.Color, ink.Color) >= 4.5,
                        theme.Id + " list containers must pair the theme surface with readable text instead of retaining Windows white chrome.");
                foreach (var item in new[] { normal, selected, ordinaryViewItem, tableItem })
                {
                    var expectedSurface = item.IsSelected ? theme.Selection : theme.Surface;
                    var expectedInk = item.IsSelected ? theme.SelectionText : theme.Ink;
                    var border = Descendants(item).OfType<Border>().First();
                    check(border.Background is SolidColorBrush actualSurface &&
                          (item.IsSelected ? actualSurface.Color == expectedSurface : actualSurface.Color.A == 0) &&
                          item.Foreground is SolidColorBrush actualInk && actualInk.Color == expectedInk &&
                          Contrast(expectedSurface, actualInk.Color) >= 4.5,
                        theme.Id + " native list item templates must paint readable normal and selected states.");
                    var hover = ((Style)app.Resources[item is ListViewItem ? typeof(ListViewItem) : typeof(ListBoxItem)])
                        .Triggers.OfType<MultiTrigger>().Single();
                    var hoverSurface = (SolidColorBrush)hover.Setters.OfType<Setter>().Single(setter => setter.Property == Control.BackgroundProperty).Value;
                    var hoverInk = (SolidColorBrush)hover.Setters.OfType<Setter>().Single(setter => setter.Property == Control.ForegroundProperty).Value;
                    check(hover.Conditions.Any(condition => condition.Property == System.Windows.Controls.Primitives.Selector.IsSelectedProperty && Equals(condition.Value, false)) &&
                          hoverSurface.Color == theme.Hover && hoverInk.Color == theme.Ink && Contrast(hoverSurface.Color, hoverInk.Color) >= 4.5,
                        theme.Id + " list hover must remain readable and preserve selected colors.");
                }
                selected.IsEnabled = false; selected.ApplyTemplate();
                check(selected.Background is SolidColorBrush disabledSurface && disabledSurface.Color == theme.Surface &&
                      selected.Foreground is SolidColorBrush disabledInk && disabledInk.Color == theme.Muted &&
                      selected.Opacity == 1d && Contrast(disabledSurface.Color, disabledInk.Color) >= 4.5,
                    theme.Id + " disabled selected list items must retain readable muted text without Windows opacity or chrome overrides.");
                selected.IsEnabled = true;
            }
            check(list.SelectedItems.Cast<string>().SequenceEqual(["Local agent 0", "Local agent 2"]),
                "The common list template must preserve multiple selection across live palette changes.");
            var plainContent = Descendants(ordinaryViewItem).OfType<ContentPresenter>().Single();
            var row = Descendants(tableItem).OfType<GridViewRowPresenter>().Single();
            check(plainContent.Visibility == Visibility.Visible && Equals(plainContent.Content, "Plain list entry") &&
                  row.Visibility == Visibility.Visible && ReferenceEquals(row.Columns, columns.Columns) && row.Content is ListRow { Name: "Fixture agent", Uses: 7 },
                "ListView theme chrome must preserve both ordinary content and the GridView's live row/column presenter.");
            list.IsEnabled = false;
            check(list.Foreground is SolidColorBrush containerInk && containerInk.Color == Ui.Muted.Color &&
                  normal.Foreground is SolidColorBrush inheritedDisabledInk && inheritedDisabledInk.Color == Ui.Muted.Color && list.Opacity == 1d,
                "Disabling a list must keep its container and child item text readable.");
            list.IsEnabled = true;
            var scroll = Descendants(list).OfType<ScrollViewer>().Single();
            scroll.ScrollToEnd(); body.UpdateLayout();
            check(scroll.ScrollableHeight > 0 && scroll.VerticalOffset > 0 && list.Items.Count == 30,
                "The common list styles must retain scrolling and all selectable entries.");
        }
        finally { window.Close(); }
    }

    private sealed record ListRow(string Name, int Uses);

    // Kept unshown: this exercises WPF logical inheritance without starting a service connection.
    private sealed class ThemeHost() : ConnectedWindow("Theme fixture", "", persistWindowPreferences: false)
    {
        protected override Task ConnectedAsync(HostInfo info) => throw new InvalidOperationException("Theme fixture must remain unshown and disconnected.");
        protected override Task<object> InspectLoadedStateAsync(string? sessionId) => throw new InvalidOperationException("Theme fixture has no service state.");
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }

    private static double Luminance(Color color) => (.2126 * color.R + .7152 * color.G + .0722 * color.B) / 255d;

    private static double Contrast(Color first, Color second)
    {
        var light = Math.Max(RelativeLuminance(first), RelativeLuminance(second));
        var dark = Math.Min(RelativeLuminance(first), RelativeLuminance(second));
        return (light + .05) / (dark + .05);
    }

    private static double RelativeLuminance(Color color)
    {
        static double Channel(byte value)
        {
            var normalized = value / 255d;
            return normalized <= .04045 ? normalized / 12.92 : Math.Pow((normalized + .055) / 1.055, 2.4);
        }
        return .2126 * Channel(color.R) + .7152 * Channel(color.G) + .0722 * Channel(color.B);
    }
}
