using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Cerebral.Desktop.Plugins;

namespace Cerebral.Orchestrator;

/// <summary>Edits the existing composer in place; optional effects never own its text, focus or lifetime.</summary>
internal sealed class ComposerDraftPresentation : IDisposable
{
    private readonly TextBox editor;
    private SpanRevealAdorner? effect;
    private AdornerLayer? layer;
    private bool applying;
    private bool disposed;

    internal ComposerDraftPresentation(TextBox editor)
    {
        this.editor = editor;
        editor.TextChanged += TextChanged;
        editor.SelectionChanged += SelectionChanged;
        editor.LostKeyboardFocus += LostFocus;
        editor.SizeChanged += SizeChanged;
        editor.IsVisibleChanged += VisibilityChanged;
        editor.Unloaded += Unloaded;
        editor.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler(Scrolled));
    }

    internal DesktopOperationResult Apply(string text, DesktopDraftPresentation presentation)
    {
        editor.Dispatcher.VerifyAccess();
        if (disposed || editor.IsReadOnly || !editor.IsEnabled || text is null ||
            text.Length > DesktopPluginContract.MaxTextChars || editor.MaxLength > 0 && text.Length > editor.MaxLength ||
            presentation.ExpectedSelection?.IsWithin(editor.Text.Length) != true ||
            presentation.Selection?.IsWithin(text.Length) != true ||
            editor.SelectionStart != presentation.ExpectedSelection.Start ||
            editor.SelectionLength != presentation.ExpectedSelection.Length)
            return new(DesktopOperationState.Rejected, "The text entry or its selection changed. Current text was preserved.");
        var previous = editor.Text;
        if (text == previous)
            return presentation.Selection == presentation.ExpectedSelection
                ? new(DesktopOperationState.Completed)
                : new(DesktopOperationState.Rejected, "An unchanged transcript cannot move the caret.");
        var (start, removed, inserted) = ChangedSpan(previous, text);
        var horizontal = editor.HorizontalOffset;
        var vertical = editor.VerticalOffset;
        ClearEffect();
        applying = true;
        try
        {
            // SelectedText uses the editor's existing text-edit and undo path.
            // Keep this asynchronous update separate from earlier and subsequent user typing.
            editor.LockCurrentUndoUnit();
            editor.BeginChange();
            try
            {
                editor.Select(start, removed);
                editor.SelectedText = text.Substring(start, inserted);
                editor.Select(presentation.Selection.Start, presentation.Selection.Length);
            }
            finally { editor.EndChange(); }
            editor.LockCurrentUndoUnit();
            editor.UpdateLayout();
            editor.ScrollToHorizontalOffset(horizontal);
            editor.ScrollToVerticalOffset(vertical);
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException)
        {
            return new(DesktopOperationState.Uncertain,
                "The editor could not confirm the update (" + error.GetType().Name + "). Review the current draft; the update must not be replayed automatically.");
        }
        finally { applying = false; }
        if (editor.Text != text || editor.SelectionStart != presentation.Selection.Start ||
            editor.SelectionLength != presentation.Selection.Length)
            return new(DesktopOperationState.Uncertain,
                "The editor did not confirm the exact text and caret. Review the current draft before continuing.");
        if (presentation.FadeChangedText && inserted > 0)
            ShowEffect(start, inserted);
        return new(DesktopOperationState.Completed);
    }

    internal static (int Start, int Removed, int Inserted) ChangedSpan(string previous, string next)
    {
        var start = 0;
        while (start < previous.Length && start < next.Length && previous[start] == next[start]) start++;
        // WPF selection boundaries must not split a UTF-16 surrogate pair.
        if (start > 0 && start < previous.Length &&
            (char.IsHighSurrogate(previous[start - 1]) && char.IsLowSurrogate(previous[start]) ||
             previous[start - 1] == '\r' && previous[start] == '\n')) start--;
        var suffix = 0;
        while (suffix < previous.Length - start && suffix < next.Length - start &&
            previous[previous.Length - suffix - 1] == next[next.Length - suffix - 1]) suffix++;
        var end = previous.Length - suffix;
        if (suffix > 0 && end > start &&
            (char.IsHighSurrogate(previous[end - 1]) && char.IsLowSurrogate(previous[end]) ||
             previous[end - 1] == '\r' && previous[end] == '\n')) suffix--;
        return (start, previous.Length - start - suffix, next.Length - start - suffix);
    }

    private void ShowEffect(int start, int length)
    {
        // Reduced animation/high contrast take precedence. Large or non-solid presentations
        // remain ordinary text edits; an optional effect cannot delay or invalidate accepted text.
        if (!SystemParameters.ClientAreaAnimation || SystemParameters.HighContrast || length > 4096 ||
            editor.Background is not SolidColorBrush { Color.A: 255, Opacity: 1 } background ||
            editor.SelectionLength != 0) return;
        try
        {
            editor.UpdateLayout();
            var currentLayer = AdornerLayer.GetAdornerLayer(editor);
            if (currentLayer is null) return;
            var rectangles = new List<Rect>();
            for (var index = start; index < start + length; index++)
            {
                var leading = editor.GetRectFromCharacterIndex(index, false);
                var trailing = editor.GetRectFromCharacterIndex(index, true);
                if (leading.IsEmpty || trailing.IsEmpty || Math.Abs(leading.Top - trailing.Top) > .5) continue;
                var width = Math.Abs(trailing.Left - leading.Left);
                if (width <= 0 || leading.Height <= 0) continue;
                var rectangle = new Rect(Math.Min(leading.Left, trailing.Left), leading.Top, width, leading.Height);
                rectangle.Intersect(new Rect(editor.RenderSize));
                if (rectangle.IsEmpty) continue;
                if (rectangles.Count > 0 && Math.Abs(rectangles[^1].Top - rectangle.Top) < .5 &&
                    Math.Abs(rectangles[^1].Right - rectangle.Left) < 1)
                {
                    var prior = rectangles[^1]; prior.Union(rectangle); rectangles[^1] = prior;
                }
                else rectangles.Add(rectangle);
                if (rectangles.Count > 32) return;
            }
            if (rectangles.Count == 0) return;
            // A short fading mask reveals the actual new glyphs, rather than drawing a second
            // transcript or changing the opacity of existing text. It never participates in input.
            var brush = background.CloneCurrentValue(); brush.Freeze();
            var adorner = new SpanRevealAdorner(editor, rectangles.ToArray(), brush);
            effect = adorner; layer = currentLayer;
            currentLayer.Add(adorner);
            var animation = new DoubleAnimation(.85, 0, TimeSpan.FromMilliseconds(160));
            animation.Completed += (_, _) => { if (ReferenceEquals(effect, adorner)) ClearEffect(); };
            adorner.BeginAnimation(UIElement.OpacityProperty, animation);
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException)
        {
            ClearEffect(); // The exact text update already succeeded; decoration has no write authority.
        }
    }

    private void ClearEffect()
    {
        var previous = effect; var previousLayer = layer; effect = null; layer = null;
        if (previous is null) return;
        try { previous.BeginAnimation(UIElement.OpacityProperty, null); }
        catch (InvalidOperationException) { /* The optional visual may already be retiring. */ }
        try { previousLayer?.Remove(previous); }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException) { /* Text is independent of this decoration. */ }
    }
    private void TextChanged(object sender, TextChangedEventArgs args) { if (!applying) ClearEffect(); }
    private void SelectionChanged(object sender, RoutedEventArgs args) { if (!applying) ClearEffect(); }
    private void Scrolled(object sender, ScrollChangedEventArgs args) { if (!applying) ClearEffect(); }
    private void LostFocus(object sender, System.Windows.Input.KeyboardFocusChangedEventArgs args) => ClearEffect();
    private void SizeChanged(object sender, SizeChangedEventArgs args) => ClearEffect();
    private void VisibilityChanged(object sender, DependencyPropertyChangedEventArgs args) { if (!editor.IsVisible) ClearEffect(); }
    private void Unloaded(object sender, RoutedEventArgs args) => ClearEffect();

    public void Dispose()
    {
        if (disposed) return;
        disposed = true; ClearEffect();
        editor.TextChanged -= TextChanged; editor.SelectionChanged -= SelectionChanged;
        editor.LostKeyboardFocus -= LostFocus; editor.SizeChanged -= SizeChanged;
        editor.IsVisibleChanged -= VisibilityChanged; editor.Unloaded -= Unloaded;
        editor.RemoveHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler(Scrolled));
    }

    private sealed class SpanRevealAdorner : Adorner
    {
        private readonly Rect[] rectangles;
        private readonly Brush background;
        public SpanRevealAdorner(TextBox editor, Rect[] rectangles, Brush background) : base(editor)
        {
            this.rectangles = rectangles; this.background = background;
            IsHitTestVisible = false; Focusable = false;
        }
        protected override void OnRender(DrawingContext drawingContext)
        {
            foreach (var rectangle in rectangles) drawingContext.DrawRectangle(background, null, rectangle);
        }
    }
}
