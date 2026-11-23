using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Xml;
using System.Xml.Linq;
using Cerebral.Desktop.Plugins;

namespace Cerebral.Orchestrator;

internal enum StatusMetricIcon { Cpu, Ram, Storage, Machine, Model, Queue }

/// <summary>Six exact Cortex-admitted Remix originals. Parsing is passive, bounded and cached once.</summary>
internal static class StatusMetricIcons
{
    private const int MaximumSvgBytes = 4096;
    private const string ResourcePrefix = "Cerebral.Orchestrator.StatusIcons.";
    private sealed record Definition(string File, string Sha256);
    internal sealed record Pack(IReadOnlyDictionary<StatusMetricIcon, Geometry> Geometries, string? Diagnostic);
    private static readonly IReadOnlyDictionary<StatusMetricIcon, Definition> Definitions =
        new Dictionary<StatusMetricIcon, Definition>
        {
            [StatusMetricIcon.Cpu] = new("cpu-line.svg", "c3f9d4128b86b8bb8028f33f125f45e7f3abc643729fd70fc63b46ced91b8629"),
            [StatusMetricIcon.Ram] = new("ram-2-line.svg", "342f1f3ac3ef036c13697a24fb1b6886e94ca6c7f9cf9801b3029ceda4e5d7ed"),
            [StatusMetricIcon.Storage] = new("hard-drive-2-line.svg", "8ef731c6702a19db270c82607d39138293b026b8a3127512876de0d46819df2d"),
            [StatusMetricIcon.Machine] = new("computer-line.svg", "41ce0e2a6928afe4e627524807603cce9021c560ed1e39ec3d5dbfb55f6471b4"),
            [StatusMetricIcon.Model] = new("robot-2-line.svg", "7c6b289f2f5ebe53b38c3862d0325d720621ae044a58a1b06691808d35f80807"),
            [StatusMetricIcon.Queue] = new("stack-line.svg", "c3d706391b37a7ce18d0b17e04cd524c37bfb58c806493e191e7dc3c95cda393")
        };
    private static readonly Lazy<Pack> Embedded = new(() => Load(name => typeof(StatusMetricIcons).Assembly.GetManifestResourceStream(name)));
    public static string? Diagnostic => Embedded.Value.Diagnostic;

    public static FrameworkElement? Create(StatusMetricIcon icon, string label)
    {
        if (!Embedded.Value.Geometries.TryGetValue(icon, out var geometry)) return null;
        var path = new System.Windows.Shapes.Path { Data = geometry };
        var canvas = new Canvas { Width = 24, Height = 24 };
        canvas.Children.Add(path);
        var control = new ContentControl
        {
            Width = 14, Height = 14, Focusable = false, IsTabStop = false,
            Padding = new Thickness(0), Margin = new Thickness(0, 0, 3, 0),
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Stretch,
            Content = new Viewbox { Child = canvas, Stretch = Stretch.Uniform },
            ToolTip = label
        };
        control.SetResourceReference(Control.ForegroundProperty, DesktopThemeResourceKeys.TextBrush);
        path.SetBinding(Shape.FillProperty, new Binding(nameof(Control.Foreground)) { Source = control });
        AutomationProperties.SetName(control, label);
        AutomationProperties.SetHelpText(control, "Remix Icon v4.7.0, Apache-2.0; verified passive Cortex artwork.");
        return control;
    }

    internal static Pack Load(Func<string, Stream?> openResource)
    {
        try
        {
            var geometries = new Dictionary<StatusMetricIcon, Geometry>();
            foreach (var (key, definition) in Definitions)
            {
                using var input = openResource(ResourcePrefix + definition.File)
                    ?? throw new InvalidDataException("Missing embedded icon: " + definition.File);
                using var bounded = new MemoryStream();
                var buffer = new byte[MaximumSvgBytes + 1];
                int read;
                while ((read = input.Read(buffer)) != 0)
                {
                    if (bounded.Length + read > MaximumSvgBytes) throw new InvalidDataException("Embedded icon exceeds 4 KiB.");
                    bounded.Write(buffer, 0, read);
                }
                geometries.Add(key, Admit(bounded.ToArray(), definition.Sha256));
            }
            return new(geometries, null);
        }
        catch (Exception error) when (error is IOException or InvalidDataException or XmlException or
            FormatException or ArgumentException or InvalidOperationException)
        {
            var detail = error.Message.Length <= 512 ? error.Message : error.Message[..512];
            return new(new Dictionary<StatusMetricIcon, Geometry>(), "Status artwork unavailable; labels retained. " + detail);
        }
    }

    internal static Geometry Admit(byte[] bytes, string expectedSha256)
    {
        if (bytes.Length is 0 or > MaximumSvgBytes ||
            !Convert.ToHexStringLower(SHA256.HashData(bytes)).Equals(expectedSha256, StringComparison.Ordinal))
            throw new InvalidDataException("Status artwork size or complete SHA-256 does not match its pinned identity.");
        var text = new UTF8Encoding(false, true).GetString(bytes);
        using var reader = XmlReader.Create(new StringReader(text), new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null,
            MaxCharactersInDocument = MaximumSvgBytes, IgnoreWhitespace = false
        });
        var document = XDocument.Load(reader);
        XNamespace svg = "http://www.w3.org/2000/svg";
        var root = document.Root;
        if (document.Declaration is not null || root?.Name != svg + "svg" ||
            document.DescendantNodes().Any(node => node is not XElement &&
                (node is not XText white || !string.IsNullOrWhiteSpace(white.Value))) ||
            root.Attribute("viewBox")?.Value != "0 0 24 24" || root.Attribute("fill")?.Value != "currentColor")
            throw new InvalidDataException("Status artwork must be one passive 24px SVG with currentColor paint.");
        foreach (var attribute in root.Attributes())
        {
            if (attribute.IsNamespaceDeclaration && attribute.Name.LocalName == "xmlns" && attribute.Value == svg.NamespaceName) continue;
            if (attribute.Name.NamespaceName.Length != 0 || attribute.Name.LocalName is not ("viewBox" or "fill"))
                throw new InvalidDataException("Unsupported SVG root attribute.");
        }
        var elements = root.Elements().ToArray();
        if (elements.Length != 1 || elements[0].Name != svg + "path" || elements[0].Elements().Any() ||
            elements[0].Attributes().Count() != 1 || elements[0].Attribute("d") is not { } data ||
            string.IsNullOrWhiteSpace(data.Value) ||
            !Regex.IsMatch(data.Value, @"^[MCLHVZ0-9.,+\-\s]+$", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100)))
            throw new InvalidDataException("Only the selected passive Remix path commands are supported.");
        // SVG uses nonzero winding by default; WPF's path mini-language otherwise defaults to evenodd.
        var geometry = Geometry.Parse("F1 " + data.Value);
        var bounds = geometry.Bounds;
        var area = geometry.GetArea();
        if (bounds.IsEmpty || !double.IsFinite(bounds.Left) || !double.IsFinite(bounds.Top) ||
            !double.IsFinite(bounds.Right) || !double.IsFinite(bounds.Bottom) || !double.IsFinite(area) || area <= 0 ||
            bounds.Left < 0 || bounds.Top < 0 || bounds.Right > 24 || bounds.Bottom > 24)
            throw new InvalidDataException("Status geometry must be nonempty and contained within its 24px canvas.");
        geometry.Freeze();
        return geometry;
    }
}

/// <summary>Reuses fixed inline groups; ordinary metric refreshes update strings, not artwork or controls.</summary>
internal sealed class StatusMetricLine
{
    private sealed record Item(string Label, Span Span, Run Value, FrameworkElement? Icon);
    private readonly TextBlock target;
    private readonly IReadOnlyList<Item> items;
    private bool mounted;

    public StatusMetricLine(TextBlock target, IReadOnlyList<(StatusMetricIcon? Icon, string Label)> definitions,
        Func<StatusMetricIcon, string, FrameworkElement?>? create = null)
    {
        this.target = target;
        create ??= StatusMetricIcons.Create;
        items = definitions.Select(definition =>
        {
            var icon = definition.Icon is { } key ? create(key, definition.Label) : null;
            var span = new Span();
            if (icon is not null)
                span.Inlines.Add(new InlineUIContainer(icon) { BaselineAlignment = BaselineAlignment.Center });
            else if (definition.Label.Length != 0)
                span.Inlines.Add(new Run(definition.Label + " "));
            var value = new Run();
            span.Inlines.Add(value);
            return new Item(definition.Label, span, value, icon);
        }).ToArray();
    }

    // Inline UI objects can paint past TextTrimming; reserve the right-side controls with an explicit clip.
    public static FrameworkElement CreateClippedView(TextBlock text)
    {
        var area = new Grid { ClipToBounds = true };
        area.Children.Add(text);
        return area;
    }

    public void SetValues(IReadOnlyList<string> values, string details)
    {
        if (values.Count != items.Count) throw new ArgumentException("Status values must match the fixed inline groups.", nameof(values));
        // WPF ellipsis can keep inline icons while eliding their adjacent value runs; the outer clip preserves order.
        target.TextTrimming = TextTrimming.None;
        if (!mounted)
        {
            target.Inlines.Clear();
            foreach (var item in items)
            {
                if (target.Inlines.Count != 0) target.Inlines.Add(new Run("  ·  "));
                target.Inlines.Add(item.Span);
            }
            mounted = true;
        }
        var accessible = new List<string>(items.Count);
        for (var index = 0; index < items.Count; index++)
        {
            var item = items[index];
            item.Value.Text = values[index];
            var label = (item.Label + " " + values[index]).Trim();
            item.Span.ToolTip = label;
            if (item.Icon is not null)
            {
                item.Icon.ToolTip = label;
                AutomationProperties.SetName(item.Icon, label);
            }
            accessible.Add(label);
        }
        var summary = string.Join(" · ", accessible);
        AutomationProperties.SetName(target, summary);
        target.ToolTip = summary + "\n\n" + details + (StatusMetricIcons.Diagnostic is { } diagnostic ? "\n\n" + diagnostic : "");
    }

    public void ShowMessage(string message, string details)
    {
        target.Text = message;
        target.TextTrimming = TextTrimming.CharacterEllipsis;
        target.ToolTip = details;
        AutomationProperties.SetName(target, message);
        mounted = false;
    }
}
