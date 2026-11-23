using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace Cerebral.Desktop;

/// <summary>Text-only Markdown rendering. Links require a click; fenced code is never executed.</summary>
public static partial class MarkdownView
{
    [GeneratedRegex("""(\[[^\]\r\n]+\]\([^\r\n)]+\)|\*\*[^*\r\n]+\*\*|\*[^*\r\n]+\*|`[^`\r\n]+`|https?://[^\s<>\[\]"\x27`*]+)""", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking, 100)]
    private static partial Regex InlinePattern();
    [GeneratedRegex(@"(?m)^\s{0,12}(?:#{1,6} |[-*] |\d{1,6}\. |> |```)", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking, 100)]
    private static partial Regex BlockFormatting();

    public static bool HasFormatting(string text) => ClipboardRichText.LooksLikeHtml(text) ||
        InlinePattern().IsMatch(text) || BlockFormatting().IsMatch(text);
    [GeneratedRegex("(//[^\\r\\n]*|#[^\\r\\n]*|\"(?:\\\\.|[^\"\\\\])*\"|'(?:\\\\.|[^'\\\\])*'|\\b(?:using|namespace|class|record|public|private|protected|internal|static|async|await|return|new|var|void|int|string|bool|if|else|for|foreach|while|try|catch|finally|throw|true|false|null|const|let|function|import|from|def|with|as|None|True|False|export|interface|type)\\b|\\b[0-9]+(?:\\.[0-9]+)?\\b)")]
    private static partial Regex CodePattern();

    public static FrameworkElement Render(string markdown, Action<string, string>? save = null, Action<string>? error = null, Action<string>? openFile = null)
    {
        if (ClipboardRichText.LooksLikeHtml(markdown) && ClipboardRichText.TryConvertHtml(markdown, out var normalized))
            markdown = normalized;
        var document = new FlowDocument();
        var lines = markdown.Replace("\r\n", "\n").Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (line.TrimStart().StartsWith("```", StringComparison.Ordinal))
            {
                var language = line.Trim()[3..].Trim(); var code = new List<string>();
                while (++i < lines.Length && !lines[i].TrimStart().StartsWith("```", StringComparison.Ordinal)) code.Add(lines[i]);
                AddCode(document, string.Join("\n", code), language, save, error); continue;
            }
            if (i + 1 < lines.Length && line.Contains('|') && IsTableDivider(lines[i + 1]))
            {
                var rows = new List<string[]> { Cells(line) }; i++;
                while (i + 1 < lines.Length && lines[i + 1].Contains('|') && !string.IsNullOrWhiteSpace(lines[i + 1])) rows.Add(Cells(lines[++i]));
                document.Blocks.Add(Table(rows, error, openFile)); continue;
            }
            if (string.IsNullOrWhiteSpace(line)) { document.Blocks.Add(new Paragraph { FontSize = 4, Margin = new Thickness(0) }); continue; }
            var heading = 0; while (heading < Math.Min(line.Length, 6) && line[heading] == '#') heading++;
            var isHeading = heading > 0 && line.Length > heading && line[heading] == ' ';
            var text = isHeading ? line[(heading + 1)..] : line;
            if (text.StartsWith("- ", StringComparison.Ordinal) || text.StartsWith("* ", StringComparison.Ordinal)) text = "•  " + text[2..];
            var block = Inline(text, error, openFile);
            if (isHeading) { block.FontSize = Math.Max(16, 25 - heading * 2); block.FontWeight = FontWeights.SemiBold; block.Margin = new Thickness(0, 9, 0, 7); }
            if (text.StartsWith("> ", StringComparison.Ordinal))
            {
                block = Inline(text[2..], error, openFile); block.Foreground = Ui.Muted;
                block.BorderBrush = Ui.Accent; block.BorderThickness = new Thickness(3, 0, 0, 0); block.Padding = new Thickness(12, 2, 0, 2);
                document.Blocks.Add(block);
            }
            else document.Blocks.Add(block);
        }
        return SelectableMarkdown.View(document);
    }

    private static bool IsTableDivider(string value) => value.Contains('-') && value.Trim().All(c => c is '|' or '-' or ':' or ' ' or '\t');
    private static string[] Cells(string value) => value.Trim().Trim('|').Split('|').Select(x => x.Trim()).ToArray();
    private static Paragraph Inline(string text, Action<string>? error, Action<string>? openFile)
    {
        var block = SelectableMarkdown.NormalBodyParagraph();
        AddInlines(block.Inlines, text, error, openFile, 0, true);
        return block;
    }

    private static void AddInlines(InlineCollection inlines, string text, Action<string>? error,
        Action<string>? openFile, int depth, bool allowLinks)
    {
        if (depth >= 8) { inlines.Add(new Run(text)); return; }
        var offset = 0;
        foreach (Match match in InlinePattern().Matches(text))
        {
            if (match.Index > offset) inlines.Add(new Run(text[offset..match.Index]));
            var token = match.Value;
            if (token.StartsWith("**", StringComparison.Ordinal))
            {
                var bold = new Bold(); AddInlines(bold.Inlines, token[2..^2], error, openFile, depth + 1, allowLinks);
                inlines.Add(bold);
            }
            else if (token.StartsWith('*'))
            {
                var italic = new Italic(); AddInlines(italic.Inlines, token[1..^1], error, openFile, depth + 1, allowLinks);
                inlines.Add(italic);
            }
            else if (token.StartsWith('`')) inlines.Add(new Run(token[1..^1]) { FontFamily = new FontFamily("Consolas"), Background = Ui.Paper });
            else if (allowLinks)
            {
                var close = token.IndexOf("](", StringComparison.Ordinal);
                var bare = !token.StartsWith('[');
                var label = bare ? TrimBareUrl(token) : token[1..close];
                var target = bare ? label : token[(close + 2)..^1].Trim();
                if (target.StartsWith('<') && target.EndsWith('>')) target = target[1..^1];
                if (Uri.TryCreate(target, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
                {
                    var link = new Hyperlink { NavigateUri = uri, Foreground = Ui.Accent, ToolTip = target };
                    AddInlines(link.Inlines, label, error, openFile, depth + 1, false);
                    link.Click += (_, e) =>
                    {
                        e.Handled = true;
                        try { Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true }); }
                        catch (Exception ex) { error?.Invoke(ex.Message); }
                    };
                    inlines.Add(link);
                }
                else if (!bare && openFile is not null && (target.StartsWith('/') || System.IO.Path.IsPathFullyQualified(target)))
                {
                    var link = new Hyperlink { Foreground = Ui.Accent, ToolTip = "Open through the agent's file tools: " + target };
                    AddInlines(link.Inlines, label, error, openFile, depth + 1, false);
                    link.Click += (_, e) => { e.Handled = true; openFile(target); }; inlines.Add(link);
                }
                else inlines.Add(new Run(bare ? token : $"{label} ({target})"));
                if (bare && label.Length < token.Length) inlines.Add(new Run(token[label.Length..]));
            }
            else inlines.Add(new Run(token));
            offset = match.Index + match.Length;
        }
        if (offset < text.Length) inlines.Add(new Run(text[offset..]));
    }

    private static string TrimBareUrl(string token)
    {
        var end = token.Length;
        while (end > 0 && token[end - 1] is '.' or ',' or ';' or '!' or '?') end--;
        var opens = token[..end].Count(c => c == '(');
        var closes = token[..end].Count(c => c == ')');
        while (end > 0 && token[end - 1] == ')' && closes > opens) { end--; closes--; }
        return token[..end];
    }

    private static void AddCode(FlowDocument document, string content, string language, Action<string, string>? save, Action<string>? error)
    {
        var actions = Ui.Bar(Ui.Text(string.IsNullOrEmpty(language) ? "CODE" : language.ToUpperInvariant(), 11, Ui.Muted),
            Ui.Button("Copy code", () => { try { Clipboard.SetText(content); } catch (Exception ex) { error?.Invoke(ex.Message); } }));
        if (save is not null) actions.Children.Add(Ui.Button("Save to workspace…", () => save(content, language)));
        actions.Margin = new Thickness(0);
        document.Blocks.Add(new BlockUIContainer(actions) { Margin = new Thickness(0, 10, 0, 5) });
        var paragraph = new Paragraph
        {
            FontFamily = new FontFamily("Cascadia Mono, Consolas"), FontSize = 13,
            Background = Ui.CodeSurface,
            Padding = new Thickness(10), Margin = new Thickness(0, 0, 0, 12), LineHeight = 19
        };
        if (language.Equals("diff", StringComparison.OrdinalIgnoreCase))
        {
            var lines = content.Split('\n');
            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                paragraph.Inlines.Add(new Run(line) { Foreground = line.StartsWith('+') ? Ui.Accent : line.StartsWith('-') ? Ui.ErrorText : Ui.Ink });
                if (i + 1 < lines.Length) paragraph.Inlines.Add(new LineBreak());
            }
        }
        else if (language.ToLowerInvariant() is "cs" or "csharp" or "js" or "javascript" or "ts" or "typescript" or "py" or "python" or "json")
        {
            var offset = 0;
            foreach (Match match in CodePattern().Matches(content))
            {
                if (match.Index > offset) paragraph.Inlines.Add(new Run(content[offset..match.Index]));
                var token = match.Value;
                var color = token.StartsWith("//", StringComparison.Ordinal) || token.StartsWith('#') ? Brushes.SeaGreen :
                    token.StartsWith('"') || token.StartsWith('\'') ? Brushes.Sienna : char.IsDigit(token[0]) ? Brushes.DarkCyan : Brushes.RoyalBlue;
                paragraph.Inlines.Add(new Run(token) { Foreground = color }); offset = match.Index + match.Length;
            }
            if (offset < content.Length) paragraph.Inlines.Add(new Run(content[offset..]));
        }
        else paragraph.Inlines.Add(new Run(content));
        document.Blocks.Add(paragraph);
    }

    private static Table Table(List<string[]> rows, Action<string>? error, Action<string>? openFile)
    {
        var table = new Table { CellSpacing = 0, Margin = new Thickness(0, 9, 0, 12) };
        var width = rows.Max(x => x.Length);
        for (var col = 0; col < width; col++) table.Columns.Add(new TableColumn());
        var group = new TableRowGroup(); table.RowGroups.Add(group);
        for (var row = 0; row < rows.Count; row++)
        {
            var output = new TableRow(); group.Rows.Add(output);
            for (var col = 0; col < width; col++)
            {
                var text = Inline(col < rows[row].Length ? rows[row][col] : "", error, openFile);
                if (row == 0) text.FontWeight = FontWeights.SemiBold;
                output.Cells.Add(new TableCell(text) { Padding = new Thickness(9), BorderBrush = Ui.Line,
                    BorderThickness = new Thickness(0, 0, 0, 1), Background = row == 0 ? Ui.Paper : Ui.Surface });
            }
        }
        return table;
    }
}
