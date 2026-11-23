using System.Text.Json;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using Cerebral.Core;
using Cerebral.Desktop;

namespace Cerebral.Orchestrator;

public sealed partial class OrchestratorWindow
{
    // Deterministic WPF hydration check. It does not connect to a service or write the user's local preference file.
    public int CheckInterfacePreferencesUi()
    {
        var checks = 0;
        void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
            checks++;
        }

        var selected = Tabs.SelectedItem is TabItem { Tag: string surface } &&
                       InterfacePreferencePolicy.Surfaces.Contains(surface) ? surface : "chat";
        var original = new InterfacePreferences
        {
            EnterSends = enterSends.IsChecked == true,
            NavigationVisible = workspaceRequested,
            PanelsVisible = activityRequested,
            SelectedSurface = selected,
            AppearanceTheme = Ui.AppearanceTheme,
            MessageTimeFormat = messageTimeFormat,
            RecentChatLimit = recentChatLimit
        };

        try
        {
            ApplyPortableInterfacePreferences(new InterfacePreferences
            {
                Revision = 7,
                EnterSends = true,
                NavigationVisible = false,
                PanelsVisible = true,
                SelectedSurface = "tools",
                AppearanceTheme = AppearanceThemePolicy.Dark,
                MessageTimeFormat = MessageTimeFormatPolicy.TwentyFourHour,
                RecentChatLimit = 15
            }, persistLocal: false);

            Check(enterSends.IsChecked == true, "Portable Enter-sends must hydrate the composer control.");
            Check(!workspaceRequested, "Portable navigation visibility must hydrate the workspace request.");
            Check(activityRequested, "Portable panel visibility must hydrate the plugin-panel request.");
            Check(Tabs.SelectedItem is TabItem { Tag: "tools" }, "Portable selected surface must hydrate the named application tab.");
            Check(Ui.AppearanceTheme == AppearanceThemePolicy.Dark && Ui.ResolvedTheme == AppearanceThemePolicy.Dark,
                "Portable appearance must hydrate the main Cerebral shell through the shared palette.");
            Check(messageTimeFormat == MessageTimeFormatPolicy.TwentyFourHour,
                "Portable message time format must hydrate the transcript renderer.");
            Check(recentChatLimit == 15, "Portable recent chat limit must hydrate the navigation policy without changing model context.");
            Check(!hydratingInterfacePreferences, "Hydration must release its event-suppression boundary.");
            // Exercise real editor changes with a fake Send callback; no focus or service submission occurs.
            static string EditorState(TextBox editor) =>
                "initialized=" + editor.IsInitialized + "; loaded=" + editor.IsLoaded +
                "; undoEnabled=" + editor.IsUndoEnabled + "; undoLimit=" + editor.UndoLimit +
                "; template=" + (editor.Template is not null) + "; contentHost=" +
                (editor.Template?.FindName("PART_ContentHost", editor) is FrameworkElement);
            foreach (var sendOnEnter in new[] { true, false })
            {
                var editor = new TextBox { AcceptsReturn = true, Text = "left RIGHT tail" };
                // WPF skips undo units until its template creates the editor TextView.
                var editorHost = new Grid(); editorHost.Children.Add(editor);
                editorHost.Measure(new Size(420, 100)); editorHost.Arrange(new Rect(0, 0, 420, 100)); editorHost.UpdateLayout();
                Check(editor.Template?.FindName("PART_ContentHost", editor) is FrameworkElement,
                    "The offscreen fixture editor has its real content host before key edits: " + EditorState(editor));
                Check(editor.IsUndoEnabled && (editor.UndoLimit == -1 || editor.UndoLimit > 0),
                    "The offscreen fixture editor admits undo before key edits: " + EditorState(editor));
                var sends = 0;
                var sendModifiers = sendOnEnter ? ModifierKeys.None : ModifierKeys.Control;
                var newlineModifiers = sendOnEnter ? ModifierKeys.Control : ModifierKeys.None;
                Check(TryHandleComposerEnter(editor, Key.Enter, sendModifiers, sendOnEnter, false, false, () => sends++) &&
                    sends == 1 && editor.Text == "left RIGHT tail",
                    "The configured Enter gesture dispatches Send once without editing the draft (Enter sends=" + sendOnEnter + ").");
                Check(TryHandleComposerEnter(editor, Key.Enter, sendModifiers, sendOnEnter, false, true, () => sends++) && sends == 1,
                    "Holding the send gesture consumes repeat without dispatching another request.");
                editor.Select(5, 5);
                Check(TryHandleComposerEnter(editor, Key.Enter, newlineModifiers, sendOnEnter, false, false, () => sends++) &&
                    editor.Text == "left " + Environment.NewLine + " tail" && editor.CaretIndex == 5 + Environment.NewLine.Length && sends == 1,
                    "The alternate gesture replaces the selection with one newline and advances the caret.");
                Check(editor.CanUndo, "The alternate newline remains an ordinary undoable editor operation: " + EditorState(editor));
                editor.Undo();
                Check(editor.Text == "left RIGHT tail", "Undo restores the text replaced by the newline: " + EditorState(editor));
                foreach (var shift in new[] { ModifierKeys.Shift, ModifierKeys.Control | ModifierKeys.Shift })
                {
                    editor.Text = "ab"; editor.CaretIndex = 1;
                    Check(TryHandleComposerEnter(editor, Key.Enter, shift, sendOnEnter, false, false, () => sends++) &&
                        editor.Text == "a" + Environment.NewLine + "b" && sends == 1,
                        "Shift+Enter remains a newline and never sends, with or without Control.");
                }
                editor.Text = "12345678"; editor.MaxLength = 8; editor.Select(4, 0);
                Check(TryHandleComposerEnter(editor, Key.Enter, newlineModifiers, sendOnEnter, false, false, () => sends++) &&
                    editor.Text == "12345678" && editor.SelectionStart == 4 && editor.SelectionLength == 0 && sends == 1,
                    "A newline that cannot fit the editor limit preserves text and selection without sending.");
                editor.Select(3, 2);
                Check(TryHandleComposerEnter(editor, Key.Enter, newlineModifiers, sendOnEnter, false, false, () => sends++) &&
                    editor.Text == "123" + Environment.NewLine + "678" && sends == 1,
                    "Replacing selected text admits a complete newline within the editor limit.");
                editor.MaxLength = 0; editor.Text = "Keep this draft";
                foreach (var other in new[] { ModifierKeys.Alt, ModifierKeys.Windows, ModifierKeys.Control | ModifierKeys.Alt })
                    Check(!TryHandleComposerEnter(editor, Key.Enter, other, sendOnEnter, false, false, () => sends++) &&
                        editor.Text == "Keep this draft" && sends == 1,
                        "Unsupported modifier combinations retain native handling without a send or edit.");
                Check(!TryHandleComposerEnter(editor, Key.Enter, sendModifiers, sendOnEnter, true, false, () => sends++) &&
                    !TryHandleComposerEnter(editor, Key.ImeProcessed, sendModifiers, sendOnEnter, false, false, () => sends++) && sends == 1,
                    "An active IME composition or its confirmation key cannot submit a partial phrase.");
                editor.IsReadOnly = true;
                Check(!TryHandleComposerEnter(editor, Key.Enter, sendModifiers, sendOnEnter, false, false, () => sends++) && sends == 1,
                    "A read-only composer cannot dispatch a keyboard send.");
                editor.IsReadOnly = false; editor.IsEnabled = false;
                Check(!TryHandleComposerEnter(editor, Key.Enter, sendModifiers, sendOnEnter, false, false, () => sends++) && sends == 1,
                    "A disabled composer cannot dispatch a keyboard send.");
            }
            Check(IsMissingPortableInterfacePreferenceCommand(new CommandResult(false, default,
                    "Unknown command: interface.preferences.get")),
                "An older Worker missing only the portable-preference command must degrade to local choices.");
            Check(!IsMissingPortableInterfacePreferenceCommand(new CommandResult(false, default,
                    "Unknown command: conversations.recent")) &&
                  !IsMissingPortableInterfacePreferenceCommand(new CommandResult(false, default,
                    "Interface preference storage failed.")),
                "The compatibility fallback must not hide unrelated missing commands or storage failures.");
            var activeText = new TextBox();
            var passiveBackground = new Grid();
            var interactiveChoice = new ComboBox();
            var popupChoice = new ComboBoxItem();
            var choiceLabel = new TextBlock(); popupChoice.Content = choiceLabel;
            Check(ShouldReleaseTextEntryFocus(passiveBackground, activeText),
                "Background clicks dismiss active text entry for automatic dictation.");
            Check(!ShouldReleaseTextEntryFocus(interactiveChoice, activeText) &&
                !ShouldReleaseTextEntryFocus(popupChoice, activeText),
                "A dropdown or popup choice owns its mouse focus transition before dictation is dismissed.");
            Check(!ShouldReleaseTextEntryFocus(passiveBackground, interactiveChoice) &&
                !ShouldReleaseTextEntryFocus(passiveBackground, popupChoice),
                "Background handling must not clear dropdown navigation or popup item focus.");
            Check(!ShouldReleaseTextEntryFocus(activeText, activeText) &&
                !ShouldReleaseTextEntryFocus(new RichTextBox(), activeText) &&
                !ShouldReleaseTextEntryFocus(new Button(), activeText),
                "Text fields and interactive buttons retain their native mouse focus handling.");
            foreach (var theme in new[] { AppearanceThemePolicy.Dark, AppearanceThemePolicy.Light })
            {
                Ui.ApplyAppearanceTheme(theme);
                var choice = new ComboBox { ItemsSource = new[] { "First", "Selected answer" }, SelectedItem = "Selected answer" };
                var host = new Grid(); host.Children.Add(choice);
                host.Measure(new Size(300, 40)); host.Arrange(new Rect(0, 0, 300, 40)); host.UpdateLayout();
                var selection = choice.Template.FindName("SelectionPresenter", choice) as ContentPresenter;
                var toggle = choice.Template.FindName("DropDownToggle", choice) as ToggleButton;
                toggle?.ApplyTemplate();
                var renderedBorder = toggle is null ? null : VisualTreeHelper.GetChild(toggle, 0) as Border;
                Check(selection?.Content is string text && text == "Selected answer" && renderedBorder?.Background is SolidColorBrush choiceSurface &&
                    choiceSurface.Color == Ui.Surface.Color && choice.Foreground is SolidColorBrush ink && Contrast(ink.Color, choiceSurface.Color) >= 4.5,
                    "The " + theme + " dropdown renders its selected value with readable ink on the actual themed surface.");
                choice.IsEditable = true; host.UpdateLayout();
                Check(choice.Template.FindName("PART_EditableTextBox", choice) is TextBox { Visibility: Visibility.Visible } &&
                    selection?.Visibility == Visibility.Collapsed && choice.Template.FindName("PART_Popup", choice) is Popup,
                    "The shared dropdown template preserves editable input and the native popup contract in " + theme + ".");
                var option = new ComboBoxItem { Content = "Selected option", IsSelected = true }; option.ApplyTemplate();
                Check(option.Foreground is SolidColorBrush itemInk && option.Background is SolidColorBrush itemSurface &&
                    Contrast(itemInk.Color, itemSurface.Color) >= 4.5,
                    "Selected popup option colors remain readable in " + theme + ".");
            }
        }
        finally
        {
            ApplyPortableInterfacePreferences(original, persistLocal: false);
            Close();
        }

        Console.WriteLine(JsonSerializer.Serialize(new
        {
            success = true,
            interfacePreferenceChecks = checks,
            evidence = "deterministic WPF hydration with no service, network, or local preference-file writes",
            serviceCommandsExecuted = false,
            windowShown = false
        }));
        return 0;
    }

    private static double Contrast(Color first, Color second)
    {
        static double Channel(byte value) { var v = value / 255d; return v <= .04045 ? v / 12.92 : Math.Pow((v + .055) / 1.055, 2.4); }
        static double Luminance(Color value) => .2126 * Channel(value.R) + .7152 * Channel(value.G) + .0722 * Channel(value.B);
        var a = Luminance(first); var b = Luminance(second); return (Math.Max(a, b) + .05) / (Math.Min(a, b) + .05);
    }
}
