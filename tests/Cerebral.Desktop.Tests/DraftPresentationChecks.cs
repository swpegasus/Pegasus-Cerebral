using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Cerebral.Desktop.Plugins;
using Cerebral.Orchestrator;

namespace Cerebral.Desktop.Tests;

internal static class DraftPresentationChecks
{
    internal static int Run(string scratch)
    {
        var count = 0;
        void Check(bool value, string name)
        {
            if (!value) throw new InvalidOperationException("Draft presentation: " + name);
            count++;
        }
        Check(!((IDesktopDefaultTextEntryHost)new LegacyDefaultHost()).SupportsDraftPresentation,
            "an old interface implementation defaults to unsupported");
        Check(new DesktopTextSelection(3, 2).IsWithin(5) && !new DesktopTextSelection(int.MaxValue, int.MaxValue).IsWithin(5) &&
            !new DesktopTextSelection(-1, 0).IsWithin(5), "selection bounds cannot overflow");

        var binding = new DesktopConversationBinding("presentation-fixture", null);
        var text = "retained"; long revision = 7; var writes = 0;
        DesktopPluginHostAdapter Host() => new(Dispatcher.CurrentDispatcher,
            () => new(binding, "Fixture", null, null, false), () => new(binding, revision, text),
            (update, _) => { writes++; text = update.Text; revision++; return Completed(); },
            (_, _) => Completed(), (_, _) => Completed(), (_, _) => Completed(), _ => { }, pluginDataRoot: scratch);
        var entry = new Entry();
        var old = Host(); old.ConfigureDefaultTextEntry(() => entry);
        Check(!old.SupportsDraftPresentation, "old configure overload does not advertise new editing behavior");
        var update = new DesktopDraftUpdate(binding, revision, "retained new")
            { Presentation = new(new(8, 0), new(12, 0), true) };
        Check(old.ReplaceDraftAsync(update).GetAwaiter().GetResult().State == DesktopOperationState.Rejected && writes == 0,
            "unsupported host refuses presentation before invoking its write delegate");
        IDesktopTextEntry? current = entry;
        var host = Host(); host.ConfigureDefaultTextEntry(() => current, supportsDraftPresentation: true);
        using var scope = host.CreateScope("fixture.presentation", "Fixture", scratch);
        var capability = (IDesktopDefaultTextEntryHost)scope;
        Check(capability.SupportsDraftPresentation, "owner opt-in is forwarded through the plugin scope");
        current = null;
        Check(!capability.SupportsDraftPresentation, "retired default entry immediately withdraws support");
        current = entry;
        Check(host.ReplaceDraftAsync(update with { ExpectedRevision = revision - 1 }).GetAwaiter().GetResult().State == DesktopOperationState.Rejected && writes == 0,
            "stale revision cannot reach a presentation writer");
        Check(host.ReplaceDraftAsync(update with { Binding = new("other-owner", null) }).GetAwaiter().GetResult().State == DesktopOperationState.Rejected && writes == 0,
            "foreign conversation cannot reach a presentation writer");
        Check(host.ReplaceDraftAsync(update with { Presentation = new(new(8, int.MaxValue), new(12, 0)) }).GetAwaiter().GetResult().State == DesktopOperationState.Rejected && writes == 0,
            "malformed expected selection cannot reach a presentation writer");
        Check(host.ReplaceDraftAsync(update).GetAwaiter().GetResult().State == DesktopOperationState.Completed && writes == 1 && revision == 8,
            "admitted presentation reaches its owner once");
        var configuration = new DesktopPluginHostAdapter(Dispatcher.CurrentDispatcher, _ => { }, scratch);
        Check(!configuration.SupportsDraftPresentation, "Configuration never advertises Main presentation");
        scope.Dispose();
        var retired = false;
        try { _ = capability.SupportsDraftPresentation; } catch (ObjectDisposedException) { retired = true; }
        Check(retired, "disposed plugin scope cannot query presentation support");

        var editor = Editor("before TARGET after"); editor.Select(7, 6);
        using var presenter = new ComposerDraftPresentation(editor);
        var focus = Keyboard.FocusedElement;
        var changes = 0; editor.TextChanged += (_, _) => changes++;
        Check(presenter.Apply("before hello after", new(new(7, 6), new(12, 0))).State == DesktopOperationState.Completed &&
            editor.Text == "before hello after" && editor.SelectionStart == 12 && editor.SelectionLength == 0 && changes == 1,
            "selected phrase changes once with the exact requested caret");
        Check(ReferenceEquals(focus, Keyboard.FocusedElement), "presentation does not take keyboard focus");
        Check(presenter.Apply("before hello there after", new(new(12, 0), new(18, 0))).State == DesktopOperationState.Completed &&
            editor.Text == "before hello there after" && editor.SelectionStart == 18 && changes == 2,
            "successive hypothesis preserves unrelated prefix and suffix");
        var retained = editor.Text; editor.Select(2, 0);
        Check(presenter.Apply("bad overwrite", new(new(18, 0), new(1, 0))).State == DesktopOperationState.Rejected && editor.Text == retained,
            "a user's caret movement refuses the update before changing text");
        editor.IsReadOnly = true;
        Check(presenter.Apply("bad overwrite", new(new(2, 0), new(1, 0))).State == DesktopOperationState.Rejected && editor.Text == retained,
            "a locked editor remains unchanged");
        editor.IsReadOnly = false;
        Check(presenter.Apply(retained, new(new(2, 0), new(3, 0))).State == DesktopOperationState.Rejected && editor.SelectionStart == 2,
            "unchanged partial cannot reposition the user's caret");
        Check(presenter.Apply(retained, new(new(2, 0), new(2, 0), true)).State == DesktopOperationState.Completed && changes == 2,
            "duplicate unchanged text is a no-op without extra revisions");

        var undo = Editor("abcDEF"); undo.Select(3, 0); undo.SelectedText = " user";
        // SelectedText leaves its programmatic insertion selected; normal typing leaves a caret.
        undo.Select(undo.SelectionStart + undo.SelectionLength, 0);
        Check(undo.CanUndo, "the arranged fixture records the preceding user edit before dictation");
        var before = undo.Text; var caret = undo.SelectionStart;
        using var undoPresenter = new ComposerDraftPresentation(undo);
        var next = before.Insert(caret, " spoken");
        Check(undoPresenter.Apply(next, new(new(caret, 0), new(caret + 7, 0))).State == DesktopOperationState.Completed,
            "dictated change uses the normal editor undo path");
        var canUndo = undo.CanUndo;
        undo.Undo(); Check(undo.Text == before, "one undo restores the preceding draft (before='" + before + "', actual='" + undo.Text + "', canUndo=" + canUndo + ")");
        undo.Undo(); Check(undo.Text == "abcDEF", "earlier user undo history is preserved");

        var unicode = Editor("a😀b"); unicode.Select(3, 0);
        using var unicodePresenter = new ComposerDraftPresentation(unicode);
        Check(unicodePresenter.Apply("a😁b", new(new(3, 0), new(3, 0))).State == DesktopOperationState.Completed && unicode.Text == "a😁b",
            "minimal replacement does not split a surrogate pair");

        var conflict = Editor("base"); conflict.Select(4, 0);
        using var conflictPresenter = new ComposerDraftPresentation(conflict);
        var injected = false;
        conflict.TextChanged += (_, _) => { if (!injected) { injected = true; conflict.AppendText(" user"); } };
        Check(conflictPresenter.Apply("base spoken", new(new(4, 0), new(11, 0))).State == DesktopOperationState.Uncertain &&
            conflict.Text.EndsWith(" user", StringComparison.Ordinal), "unconfirmed write preserves a concurrent owner edit without rollback");

        var fadeEditor = Editor("kept "); fadeEditor.Select(5, 0);
        var decorator = new AdornerDecorator { Child = fadeEditor };
        decorator.Measure(new Size(360, 100)); decorator.Arrange(new Rect(0, 0, 360, 100)); decorator.UpdateLayout();
        using var fadePresenter = new ComposerDraftPresentation(fadeEditor);
        Check(fadePresenter.Apply("kept new words", new(new(5, 0), new(14, 0), true)).State == DesktopOperationState.Completed &&
            fadeEditor.Opacity == 1 && fadeEditor.Text == "kept new words", "fade never changes the whole editor opacity or delays actual text");
        var adorners = AdornerLayer.GetAdornerLayer(fadeEditor)?.GetAdorners(fadeEditor) ?? [];
        Check(adorners.Length == (SystemParameters.ClientAreaAnimation && !SystemParameters.HighContrast ? 1 : 0),
            "changed-span reveal is attached when animation is permitted and omitted for accessibility settings");
        Check(adorners.All(value => !value.IsHitTestVisible && !value.Focusable), "optional fade visuals cannot intercept input");
        fadePresenter.Dispose();
        Check((AdornerLayer.GetAdornerLayer(fadeEditor)?.GetAdorners(fadeEditor)?.Length ?? 0) == 0,
            "disposing presentation removes its optional visual");
        return count;
    }

    private static Task<DesktopOperationResult> Completed() => Task.FromResult(new DesktopOperationResult(DesktopOperationState.Completed));
    private static TextBox Editor(string text)
    {
        var editor = new TextBox
        {
            Text = text, Width = 360, Height = 100, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap,
            Background = new SolidColorBrush(Color.FromRgb(24, 37, 55)), Foreground = Brushes.White,
            FontSize = 14, Padding = new Thickness(7), IsUndoEnabled = true
        };
        // No Application/theme is running in this executable. Supply the ordinary content
        // host explicitly so WPF creates a TextView; without it WPF discards undo records.
        editor.Template = new ControlTemplate(typeof(TextBox))
        {
            VisualTree = new FrameworkElementFactory(typeof(ScrollViewer), "PART_ContentHost")
        };
        // Main already has a text view when plugins can write. Build that same view without
        // showing a window, giving the fixture WPF's normal editing and undo infrastructure.
        editor.ApplyTemplate();
        editor.Measure(new Size(360, 100)); editor.Arrange(new Rect(0, 0, 360, 100)); editor.UpdateLayout();
        return editor;
    }
    private sealed class LegacyDefaultHost : IDesktopDefaultTextEntryHost
    {
        public IDesktopTextEntry? DefaultTextEntry => null;
    }
    private sealed class Entry : IDesktopTextEntry
    {
        public string Id => "composer"; public string Purpose => "Fixture"; public string Text => "retained";
        public int SelectionStart => 8; public int SelectionLength => 0;
        public Task<DesktopOperationResult> InsertAsync(string text, CancellationToken token = default) => Completed();
    }
}
