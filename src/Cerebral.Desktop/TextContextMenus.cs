using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Markup;

namespace Cerebral.Desktop;

/// <summary>Installs the ordinary Windows editing menu on host and plugin text fields.</summary>
public static class TextContextMenus
{
    private static bool installed;

    public static void Install()
    {
        if (installed) return;
        installed = true;
        EventManager.RegisterClassHandler(typeof(TextBoxBase), FrameworkElement.LoadedEvent,
            new RoutedEventHandler((sender, _) =>
            {
                if (sender is TextBoxBase { ContextMenu: null } editor) Attach(editor);
            }), true);
    }

    public static ContextMenu Attach(TextBoxBase editor, Action<ContextMenu>? append = null)
    {
        var menu = new ContextMenu();
        void Refresh()
        {
            menu.Items.Clear();
            if (!editor.IsReadOnly)
            {
                if (editor is TextBox text) AddSpellingSuggestions(menu, text);
                Add(menu, editor, "_Undo", ApplicationCommands.Undo, "TextContextUndo");
                Add(menu, editor, "_Redo", ApplicationCommands.Redo, "TextContextRedo");
                menu.Items.Add(new Separator());
                Add(menu, editor, "Cu_t", ApplicationCommands.Cut, "TextContextCut");
            }
            Add(menu, editor, "_Copy", ApplicationCommands.Copy, "TextContextCopy");
            if (!editor.IsReadOnly)
            {
                Add(menu, editor, "_Paste", ApplicationCommands.Paste, "TextContextPaste");
                Add(menu, editor, "_Delete", ApplicationCommands.Delete, "TextContextDelete");
            }
            menu.Items.Add(new Separator());
            Add(menu, editor, "Select _all", ApplicationCommands.SelectAll, "TextContextSelectAll");
            append?.Invoke(menu);
        }

        Refresh();
        menu.Opened += (_, _) => Refresh();
        editor.ContextMenu = menu;
        return menu;
    }

    /// <summary>Enables Windows' local spelling marks and right-click replacements without sending text to a model.</summary>
    public static void EnableWritingAssistance(TextBox editor)
    {
        ArgumentNullException.ThrowIfNull(editor);
        SpellCheck.SetIsEnabled(editor, true);
        try { editor.Language = XmlLanguage.GetLanguage(CultureInfo.CurrentUICulture.IetfLanguageTag); }
        catch (ArgumentException) { }
        AutomationProperties.SetHelpText(editor, "Misspelled words are underlined in red. Right-click an underlined word for local correction suggestions.");
        if (editor.ContextMenu is null) Attach(editor);
    }

    private static void AddSpellingSuggestions(ContextMenu menu, TextBox editor)
    {
        if (!SpellCheck.GetIsEnabled(editor) || editor.Text.Length == 0) return;
        var index = editor.GetCharacterIndexFromPoint(Mouse.GetPosition(editor), true);
        if (index < 0 || index >= editor.Text.Length) index = Math.Clamp(editor.CaretIndex, 0, editor.Text.Length - 1);
        var error = editor.GetSpellingError(index);
        if (error is null && index > 0) { index--; error = editor.GetSpellingError(index); }
        if (error is null) return;
        var start = editor.GetSpellingErrorStart(index);
        var length = editor.GetSpellingErrorLength(index);
        foreach (var suggestion in error.Suggestions.Cast<string>().Where(x => !string.IsNullOrWhiteSpace(x)).Take(8))
        {
            var replacement = suggestion;
            var item = new MenuItem { Header = replacement, FontWeight = FontWeights.SemiBold };
            item.Click += (_, _) =>
            {
                editor.Select(start, length); editor.SelectedText = replacement;
                editor.CaretIndex = start + replacement.Length; editor.SelectionLength = 0; editor.Focus();
            };
            menu.Items.Add(item);
        }
        if (menu.Items.Count == 0) menu.Items.Add(new MenuItem { Header = "No spelling suggestions", IsEnabled = false });
        menu.Items.Add(new Separator());
    }

    private static void Add(ContextMenu menu, TextBoxBase target, string label, ICommand command, string automationId)
    {
        var item = new MenuItem { Header = label, Command = command, CommandTarget = target };
        AutomationProperties.SetAutomationId(item, automationId);
        menu.Items.Add(item);
    }
}
