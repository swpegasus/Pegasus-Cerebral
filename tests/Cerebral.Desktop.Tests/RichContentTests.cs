using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using Cerebral.Desktop;

namespace Cerebral.Desktop.Tests;

public static class RichContentTests
{
    public static int Run()
    {
        var count = 0;
        void Check(bool condition, string label)
        {
            if (!condition) throw new InvalidOperationException(label);
            count++;
        }
        const string copied = """
            <div><h2>Top Free &amp; Open Source</h2><ul>
            <li><a href="https://openmoji.org/"><strong>OpenMoji</strong></a>: café <a href="https://github.com/hfg-gmuend/openmoji">1</a></li>
            <li><a href="https://github.com/microsoft/fluentui-emoji">Fluent Emoji</a></li>
            </ul></div>
            """;
        Check(ClipboardRichText.TryConvertHtml(copied, out var markdown) &&
            markdown.Contains("## Top Free & Open Source", StringComparison.Ordinal) &&
            markdown.Contains("[**OpenMoji**](https://openmoji.org/)", StringComparison.Ordinal) &&
            markdown.Contains("- [Fluent Emoji]", StringComparison.Ordinal) &&
            markdown.Contains("café", StringComparison.Ordinal), "Copied browser content keeps heading, list, label, Unicode and exact link target.");
        var rendered = (RichTextBox)MarkdownView.Render(markdown);
        var links = AllInlines(rendered.Document).OfType<Hyperlink>().ToArray();
        Check(links.Length == 3 && links[0].NavigateUri?.AbsoluteUri == "https://openmoji.org/" &&
            new TextRange(links[0].ContentStart, links[0].ContentEnd).Text == "OpenMoji" &&
            links[0].Inlines.OfType<Bold>().Any(), "Native renderer keeps clickable targets and nested bold labels without showing Markdown delimiters.");
        Check(AllInlines(((RichTextBox)MarkdownView.Render(copied)).Document).OfType<Hyperlink>().Count() == 3,
            "Known passive HTML fragments use the same readable link renderer.");
        Check(MarkdownView.HasFormatting(markdown) && MarkdownView.HasFormatting(copied) &&
            MarkdownView.HasFormatting("Use https://example.test/docs.") &&
            !MarkdownView.HasFormatting("var values = new List<string>();"), "Rich detail detection recognizes actual formatting and leaves ordinary code plain.");

        const string unsafeHtml = "<p>Visible <a href='javascript:alert(1)'>Unsafe</a><a href='https://example.test/safe'>Safe</a></p><script>secret script</script><style>secret style</style><iframe>hidden frame</iframe>";
        Check(ClipboardRichText.TryConvertHtml(unsafeHtml, out var sanitized) &&
            sanitized.Contains("Unsafe", StringComparison.Ordinal) && !sanitized.Contains("javascript:", StringComparison.Ordinal) &&
            !sanitized.Contains("secret", StringComparison.Ordinal) && !sanitized.Contains("hidden frame", StringComparison.Ordinal),
            "HTML is passive text: script, style, frames and unsafe navigation targets are not retained.");
        Check(AllInlines(((RichTextBox)MarkdownView.Render(sanitized)).Document).OfType<Hyperlink>()
            .All(x => x.NavigateUri?.Scheme is "http" or "https"), "Sanitized content has only explicit HTTP(S) navigation.");
        var bare = (RichTextBox)MarkdownView.Render("See https://example.test/docs, and https://example.test/path(part).");
        Check(AllInlines(bare.Document).OfType<Hyperlink>().Select(x => x.NavigateUri?.AbsoluteUri)
            .SequenceEqual(new[] { "https://example.test/docs", "https://example.test/path(part)" }),
            "Bare links retain balanced parentheses and leave sentence punctuation outside navigation.");
        Check(!AllInlines(((RichTextBox)MarkdownView.Render("[unsafe](javascript:alert)")).Document).OfType<Hyperlink>().Any(),
            "Markdown cannot create a javascript link.");
        Check(!AllInlines(((RichTextBox)MarkdownView.Render("```txt\nhttps://example.test/literal\n```")).Document).OfType<Hyperlink>().Any(),
            "Code fences remain literal and do not turn code into links.");

        const string fragment = "<p>naïve café <a href='/docs?q=1&amp;x=2'>relative</a></p>";
        var cfHtml = CfHtml(fragment);
        Check(ClipboardRichText.TryConvertHtml(cfHtml, out var fromBytes) &&
            fromBytes.Contains("naïve café", StringComparison.Ordinal) &&
            fromBytes.Contains("https://example.test/docs?q=1&x=2", StringComparison.Ordinal) &&
            !fromBytes.Contains("outside fragment", StringComparison.Ordinal) &&
            !fromBytes.Contains("Version:", StringComparison.Ordinal), "CF_HTML byte offsets preserve non-ASCII content and resolve relative targets against explicit source URL.");
        Check(ClipboardRichText.TryConvertHtml(cfHtml.Replace("StartFragment:", "BrokenFragment:", StringComparison.Ordinal), out var fromMarkers) &&
            fromMarkers == fromBytes, "Exact fragment markers recover valid copied content when offset metadata is absent.");
        Check(!ClipboardRichText.TryConvertHtml(new string('x', ClipboardRichText.MaximumHtmlCharacters + 1), out _) &&
            !ClipboardRichText.TryConvertHtml("<p>" + new string('x', ClipboardRichText.MaximumMarkdownCharacters + 1) + "</p>", out _) &&
            !ClipboardRichText.TryConvertHtml(string.Concat(Enumerable.Repeat("<b>x</b>", 4000)), out _),
            "Input, generated text and token limits reject oversized formatting instead of silently cutting content.");

        var original = new DataObject(); original.SetData(DataFormats.UnicodeText, "plain fallback"); original.SetData(DataFormats.Html, copied);
        var paste = new DataObjectPastingEventArgs(original, false, DataFormats.UnicodeText);
        Check(ClipboardRichText.PreparePaste(paste) && paste.FormatToApply == DataFormats.UnicodeText &&
            (string?)paste.DataObject.GetData(DataFormats.UnicodeText) == markdown &&
            (string?)original.GetData(DataFormats.UnicodeText) == "plain fallback",
            "Paste substitution preserves editable Markdown and does not mutate original clipboard data.");
        var fallback = new DataObject(); fallback.SetData(DataFormats.UnicodeText, "keep original");
        fallback.SetData(DataFormats.Html, new string('x', ClipboardRichText.MaximumHtmlCharacters + 1));
        var rejected = new DataObjectPastingEventArgs(fallback, false, DataFormats.UnicodeText);
        Check(!ClipboardRichText.PreparePaste(rejected) && ReferenceEquals(rejected.DataObject, fallback) &&
            (string?)rejected.DataObject.GetData(DataFormats.UnicodeText) == "keep original",
            "Unsupported or oversized HTML preserves the available ordinary text paste.");
        return count;
    }

    private static string CfHtml(string fragment)
    {
        const string before = "<html><body>outside fragment café<!--StartFragment-->";
        const string after = "<!--EndFragment-->outside fragment</body></html>";
        string Header(int start, int end) => $"Version:1.0\r\nStartHTML:{Encoding.UTF8.GetByteCount(HeaderTemplate):D10}\r\nEndHTML:{end + Encoding.UTF8.GetByteCount(after):D10}\r\nStartFragment:{start:D10}\r\nEndFragment:{end:D10}\r\nSourceURL:https://example.test/page\r\n";
        var start = Encoding.UTF8.GetByteCount(HeaderTemplate + before);
        var end = start + Encoding.UTF8.GetByteCount(fragment);
        return Header(start, end) + before + fragment + after;
    }
    private const string HeaderTemplate = "Version:1.0\r\nStartHTML:0000000000\r\nEndHTML:0000000000\r\nStartFragment:0000000000\r\nEndFragment:0000000000\r\nSourceURL:https://example.test/page\r\n";

    private static IEnumerable<Inline> AllInlines(FlowDocument document)
    {
        foreach (var paragraph in document.Blocks.OfType<Paragraph>())
            foreach (var inline in Walk(paragraph.Inlines)) yield return inline;
        static IEnumerable<Inline> Walk(InlineCollection values)
        {
            foreach (var value in values)
            {
                yield return value;
                if (value is Span span)
                    foreach (var child in Walk(span.Inlines)) yield return child;
            }
        }
    }
}
