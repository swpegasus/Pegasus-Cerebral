using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;

namespace Cerebral.Desktop;

/// <summary>Converts passive browser clipboard fragments to editable text without fetching or executing HTML.</summary>
public static partial class ClipboardRichText
{
    public const int MaximumHtmlCharacters = 256 * 1024;
    public const int MaximumMarkdownCharacters = 64 * 1024;
    private const int MaximumTokens = 10_000;

    [GeneratedRegex("""<!--[\s\S]*?-->|<![^>]*>|<(?<close>/)?(?<name>[A-Za-z][A-Za-z0-9:-]*)(?<attributes>(?:[^"'<>]|"[^"]*"|'[^']*')*)>|(?<text>[^<]+)|<""", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking, 100)]
    private static partial Regex Tokens();
    [GeneratedRegex("""(?<name>[A-Za-z_:][A-Za-z0-9_.:-]*)\s*=\s*(?:"(?<double>[^"]*)"|'(?<single>[^']*)'|(?<bare>[^\s"'=<>\x60]+))""", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking, 100)]
    private static partial Regex Attributes();
    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking, 100)]
    private static partial Regex Spaces();
    [GeneratedRegex(@"^(?:<(?:html|body|div|p|ul|ol|li|a|table|h[1-6])(?:\s|>)|<!--StartFragment-->)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking, 100)]
    private static partial Regex HtmlStart();

    public static bool LooksLikeHtml(string value) =>
        value.Length <= MaximumHtmlCharacters &&
        (HtmlStart().IsMatch(value.TrimStart()) ||
         value.StartsWith("Version:", StringComparison.OrdinalIgnoreCase) && value.Contains("StartHTML:", StringComparison.OrdinalIgnoreCase));

    /// <summary>Changes this paste only; the system clipboard and original source object remain untouched.</summary>
    public static bool PreparePaste(DataObjectPastingEventArgs args)
    {
        if (!args.SourceDataObject.GetDataPresent(DataFormats.Html) ||
            args.SourceDataObject.GetData(DataFormats.Html) is not string html ||
            !TryConvertHtml(html, out var markdown)) return false;
        args.DataObject = new DataObject(DataFormats.UnicodeText, markdown);
        args.FormatToApply = DataFormats.UnicodeText;
        return true;
    }

    public static bool TryConvertHtml(string html, out string markdown)
    {
        markdown = "";
        if (string.IsNullOrWhiteSpace(html) || html.Length > MaximumHtmlCharacters) return false;
        try
        {
            var fragment = Fragment(html, out var origin);
            var output = new StringBuilder();
            var lists = new Stack<(bool Ordered, int Next)>();
            string? hidden = null;
            (int Start, string Target)? anchor = null;
            var preformatted = false;
            var tokens = 0;
            void Break(int count = 1)
            {
                if (output.Length == 0) return;
                var existing = 0;
                for (var i = output.Length - 1; i >= 0 && output[i] == '\n'; i--) existing++;
                if (existing < count) output.Append('\n', count - existing);
            }
            void CloseAnchor()
            {
                if (anchor is not { } saved) return;
                if (output.Length > saved.Start)
                {
                    output.Insert(saved.Start, '[');
                    output.Append("](").Append(saved.Target).Append(')');
                }
                anchor = null;
            }
            foreach (Match token in Tokens().Matches(fragment))
            {
                if (++tokens > MaximumTokens || output.Length > MaximumMarkdownCharacters) return false;
                if (token.Groups["text"].Success)
                {
                    if (hidden is null)
                    {
                        var text = WebUtility.HtmlDecode(token.Value);
                        output.Append(preformatted ? text : Spaces().Replace(text, " "));
                    }
                    continue;
                }
                if (!token.Groups["name"].Success) continue;
                var tag = token.Groups["name"].Value.ToLowerInvariant();
                var closing = token.Groups["close"].Success;
                if (hidden is not null)
                {
                    if (closing && tag == hidden) hidden = null;
                    continue;
                }
                if (!closing && tag is "script" or "style" or "iframe" or "object" or "noscript" or "head" or "template" or "svg")
                {
                    hidden = tag; continue;
                }
                if (tag == "a")
                {
                    if (closing) CloseAnchor();
                    else
                    {
                        CloseAnchor();
                        if (SafeTarget(Attribute(token.Groups["attributes"].Value, "href"), origin) is { } target)
                            anchor = (output.Length, target);
                    }
                }
                else if (tag is "ul" or "ol")
                {
                    Break();
                    if (closing) { if (lists.Count > 0) lists.Pop(); }
                    else { if (lists.Count >= 32) return false; lists.Push((tag == "ol", 1)); }
                }
                else if (tag == "li")
                {
                    Break();
                    if (!closing)
                    {
                        output.Append(' ', Math.Min(Math.Max(lists.Count - 1, 0), 6) * 2);
                        if (lists.Count > 0 && lists.Peek().Ordered)
                        {
                            var current = lists.Pop(); output.Append(current.Next).Append(". ");
                            lists.Push((true, current.Next + 1));
                        }
                        else output.Append("- ");
                    }
                }
                else if (tag.Length == 2 && tag[0] == 'h' && tag[1] is >= '1' and <= '6')
                {
                    Break(2);
                    if (!closing) output.Append('#', tag[1] - '0').Append(' ');
                }
                else if (tag is "p" or "div" or "section" or "article" or "blockquote") Break(2);
                else if (tag is "br" or "tr") Break();
                else if (tag is "td" or "th") { if (closing) output.Append("  "); }
                else if (tag == "hr") { Break(2); output.Append("---"); Break(2); }
                else if (tag is "strong" or "b") output.Append("**");
                else if (tag is "em" or "i") output.Append('*');
                else if (tag == "pre")
                {
                    Break();
                    output.Append("```"); Break();
                    preformatted = !closing;
                }
                else if (tag == "code" && !preformatted) output.Append('`');
                else if (!closing && tag == "img") output.Append(WebUtility.HtmlDecode(Attribute(token.Groups["attributes"].Value, "alt") ?? ""));
            }
            CloseAnchor();
            markdown = output.ToString().Trim();
            return markdown.Length is > 0 and <= MaximumMarkdownCharacters;
        }
        catch (Exception error) when (error is RegexMatchTimeoutException or ArgumentException or EncoderFallbackException or DecoderFallbackException)
        {
            markdown = ""; return false;
        }
    }

    private static string? Attribute(string attributes, string name)
    {
        foreach (Match attribute in Attributes().Matches(attributes))
            if (attribute.Groups["name"].Value.Equals(name, StringComparison.OrdinalIgnoreCase))
                return WebUtility.HtmlDecode(attribute.Groups["double"].Success ? attribute.Groups["double"].Value :
                    attribute.Groups["single"].Success ? attribute.Groups["single"].Value : attribute.Groups["bare"].Value);
        return null;
    }

    private static string? SafeTarget(string? target, Uri? origin)
    {
        if (string.IsNullOrWhiteSpace(target) || target.Length > 4096 || target.Any(char.IsControl)) return null;
        Uri? uri = null;
        if (!Uri.TryCreate(target.Trim(), UriKind.Absolute, out uri) && origin is not null)
            Uri.TryCreate(origin, target.Trim(), out uri);
        if (uri?.Scheme is not ("http" or "https")) return null;
        return uri.AbsoluteUri.Replace("(", "%28", StringComparison.Ordinal).Replace(")", "%29", StringComparison.Ordinal);
    }

    private static string Fragment(string html, out Uri? origin)
    {
        origin = null;
        if (html.StartsWith("Version:", StringComparison.OrdinalIgnoreCase))
        {
            // CF_HTML offsets count UTF-8 bytes, not UTF-16 characters.
            var header = html[..Math.Min(html.Length, 4096)];
            foreach (var line in header.Split('\n'))
                if (line.StartsWith("SourceURL:", StringComparison.OrdinalIgnoreCase) &&
                    Uri.TryCreate(line[10..].Trim(), UriKind.Absolute, out var parsed) && parsed.Scheme is "http" or "https")
                    origin = parsed;
            int? Offset(string name)
            {
                foreach (var line in header.Split('\n'))
                    if (line.StartsWith(name + ":", StringComparison.OrdinalIgnoreCase) &&
                        int.TryParse(line[(name.Length + 1)..].Trim(), out var value)) return value;
                return null;
            }
            if (Offset("StartFragment") is { } start && Offset("EndFragment") is { } end)
            {
                var utf8 = new UTF8Encoding(false, true);
                var bytes = utf8.GetBytes(html);
                if (start >= 0 && end > start && end <= bytes.Length)
                {
                    try { return utf8.GetString(bytes, start, end - start); }
                    catch (DecoderFallbackException) { /* Invalid byte geometry falls back to exact fragment markers. */ }
                }
            }
        }
        const string begin = "<!--StartFragment-->", finish = "<!--EndFragment-->";
        var from = html.IndexOf(begin, StringComparison.OrdinalIgnoreCase);
        var to = html.IndexOf(finish, StringComparison.OrdinalIgnoreCase);
        if (from >= 0 && to > from) return html[(from + begin.Length)..to];
        return html;
    }
}
