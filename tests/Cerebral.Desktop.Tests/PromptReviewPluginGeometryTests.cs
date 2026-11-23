using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Cerebral.Desktop;

namespace Cerebral.Desktop.Tests;

internal static partial class PromptReviewPluginTests
{
    internal sealed record FooterGeometrySnapshot(
        string Scenario, double FrameWidth, double FrameHeight, double ViewportWidth,
        double ViewportHeight, double BodyOffset, double BodyScrollableHeight,
        IReadOnlyDictionary<string, double[]> Actions);
    internal sealed record FooterGeometryResult(int Checks, IReadOnlyList<FooterGeometrySnapshot> Snapshots);

    // Real WPF measure/arrange and the fixed R5 host adapter, with deterministic model replies.
    // This reproduces the host's scrolling mount and star allocation, not a visible app window.
    internal static FooterGeometryResult RunFooterGeometry(string packageDirectory, string previewDirectory)
    {
        Ui.ApplyTheme(Application.Current);
        var package = Path.GetFullPath(packageDirectory);
        var original = string.Join('\n', Enumerable.Range(0, 80).Select(i => $"Original café 😀 line {i}: preserve C:\\Fixture\\notes.txt."));
        var interpreted = string.Join('\n', Enumerable.Range(0, 80).Select(i => $"Edited café 😀 line {i}: preserve all notes and exact paths."));
        using var fixture = new Fixture(LoadReviewType(package), package, original, interpreted)
        {
            ReplyMessage = string.Join(' ', Enumerable.Repeat("Fixture discussion preserves your constraints.", 65))
        };
        var card = fixture.Review();
        var body = Find<ScrollViewer>(card, "PromptReview.BodyScroll");
        var actions = new[] { "PromptReview.Refine", "PromptReview.UseDraft", "PromptReview.Cancel" }
            .ToDictionary(id => id, id => Find<Button>(card, id));
        var outer = new ScrollViewer
        {
            Content = card, CanContentScroll = false,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalAlignment = VerticalAlignment.Stretch
        };
        var host = new Border
        {
            Child = outer, Padding = new Thickness(10), Margin = new Thickness(0, 0, 0, 8),
            BorderThickness = new Thickness(1), BorderBrush = Ui.Accent
        };
        var frame = new Grid { Background = Ui.Surface };
        frame.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(220) });
        frame.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        frame.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(180) });
        frame.RowDefinitions.Add(new RowDefinition { Height = new GridLength(60) });
        frame.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        var middle = new Grid();
        middle.RowDefinitions.Add(new RowDefinition { Height = new GridLength(48) });
        middle.RowDefinitions.Add(new RowDefinition { Height = new GridLength(36) });
        middle.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        middle.RowDefinitions.Add(new RowDefinition { Height = new GridLength(5) });
        middle.RowDefinitions.Add(new RowDefinition { Height = new GridLength(2, GridUnitType.Star) });
        Grid.SetColumn(middle, 1); Grid.SetRow(middle, 1); frame.Children.Add(middle);
        Grid.SetRow(host, 4); middle.Children.Add(host);

        var checks = 0;
        var snapshots = new List<FooterGeometrySnapshot>();
        void Check(bool condition, string detail)
        {
            if (!condition) throw new InvalidOperationException("Footer geometry: " + detail);
            checks++;
        }
        Dictionary<string, double[]> Capture(string scenario, Size size)
        {
            SettleLayout(frame, size);
            Check(double.IsFinite(card.Height) && Math.Abs(card.ActualHeight - outer.ViewportHeight) < 1,
                $"{scenario}: card must match its finite outer viewport; card={card.ActualHeight}, viewport={outer.ViewportHeight}, bound={card.Height}.");
            Check(body.ViewportHeight > 0 && body.ScrollableHeight > 0 && outer.ScrollableHeight < 1,
                $"{scenario}: body must scroll with no outer overflow; body={body.ViewportHeight}/{body.ScrollableHeight}, outer={outer.ScrollableHeight}.");
            var bounds = new Dictionary<string, double[]>();
            foreach (var (id, button) in actions)
            {
                var rect = button.TransformToAncestor(outer).TransformBounds(new Rect(button.RenderSize));
                Check(button.Visibility == Visibility.Visible && button.ActualWidth > 0 && button.ActualHeight > 0 &&
                    rect.Left >= -.5 && rect.Top >= -.5 &&
                    rect.Right <= outer.ViewportWidth + .5 && rect.Bottom <= outer.ViewportHeight + .5,
                    $"{scenario}: {id} must be wholly inside the viewport; bounds={rect}, viewport={outer.ViewportWidth}x{outer.ViewportHeight}.");
                bounds[id] = [rect.X, rect.Y, rect.Width, rect.Height];
            }
            snapshots.Add(new(scenario, size.Width, size.Height, outer.ViewportWidth, outer.ViewportHeight,
                body.VerticalOffset, body.ScrollableHeight, bounds));
            return bounds;
        }
        void CheckScrollPositions(string scenario, Size size)
        {
            body.ScrollToTop();
            var top = Capture(scenario + "-top", size);
            body.ScrollToVerticalOffset(body.ScrollableHeight / 2);
            var middleBounds = Capture(scenario + "-middle", size);
            Check(body.VerticalOffset > 0 && body.VerticalOffset < body.ScrollableHeight, scenario + ": middle offset must move content.");
            body.ScrollToEnd();
            var end = Capture(scenario + "-end", size);
            Check(Math.Abs(body.VerticalOffset - body.ScrollableHeight) < 1, scenario + ": end offset must reach the content end.");
            foreach (var id in actions.Keys)
                Check(Math.Abs(top[id][1] - middleBounds[id][1]) < .5 && Math.Abs(top[id][1] - end[id][1]) < .5,
                    scenario + ": scrolling must not move " + id);
        }

        try
        {
            var wide = new Size(1280, 840);
            var narrow = new Size(720, 540);
            CheckScrollPositions("wide-collapsed", wide);
            Find<Expander>(card, "PromptReview.OriginalSection").IsExpanded = true;
            Find<Expander>(card, "PromptReview.Differences").IsExpanded = true;
            CheckScrollPositions("wide-expanded", wide);
            Directory.CreateDirectory(previewDirectory);
            RenderFooterFixture(frame, wide, Path.Combine(previewDirectory, "prompt-review-footer-1280x840.png"));

            CheckScrollPositions("narrow-resized", narrow);
            var rows = snapshots.Last().Actions.Values.Select(rect => rect[1]).ToArray();
            Check(rows.Max() - rows.Min() > 1, "The narrow fixture must actually exercise footer wrapping.");
            RenderFooterFixture(frame, narrow, Path.Combine(previewDirectory, "prompt-review-footer-720x540.png"));
            CheckScrollPositions("wide-restored", wide);
            Check(actions.Values.All(button => button.Focusable && button.IsTabStop),
                "Existing actions must retain keyboard focusability and tab stops.");
            Check(fixture.Draft.Text == original && fixture.Draft.Revision == 4 &&
                fixture.Submissions.Count == 0 && fixture.Replacements.Count == 0 &&
                fixture.Dismissed == 0 && !fixture.GenerationToken.IsCancellationRequested,
                "Scrolling, expansion and resize must not alter the draft, submit, dismiss or cancel review.");

            var editor = Find<TextBox>(card, "PromptReview.Interpretation");
            editor.Text = "Exact footer approval café 😀.";
            Click(card, "PromptReview.UseDraft");
            Check(fixture.Draft.Text == editor.Text && fixture.Replacements.Count == 1 &&
                fixture.Replacements[0].ExpectedRevision == 4 && fixture.Submissions.Count == 0 && fixture.Dismissed == 1,
                "The mounted footer must retain exact captured-draft Apply without Send.");
            return new(checks, snapshots);
        }
        finally { outer.Content = null; host.Child = null; }
    }

    private static void SettleLayout(FrameworkElement root, Size size)
    {
        for (var pass = 0; pass < 4; pass++)
        {
            root.Measure(size); root.Arrange(new Rect(new Point(), size)); root.UpdateLayout();
            var pending = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => pending.Continue = false));
            Dispatcher.PushFrame(pending);
        }
    }

    private static void RenderFooterFixture(FrameworkElement root, Size size, string path)
    {
        SettleLayout(root, size);
        var bitmap = new RenderTargetBitmap((int)size.Width, (int)size.Height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(root);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = File.Create(path);
        encoder.Save(output);
    }
}
