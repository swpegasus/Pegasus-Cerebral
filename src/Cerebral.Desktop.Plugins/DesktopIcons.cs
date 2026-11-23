using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Shapes;

namespace Cerebral.Desktop.Plugins;

/// <summary>Semantic icon identities. Shapes supplement precise state text and never replace product branding.</summary>
public static class DesktopIconKeys
{
    public const string Success = "status.success";
    public const string Error = "status.error";
    public const string Warning = "status.warning";
    public const string Working = "status.working";
    public const string Info = "status.info";
    public const string Cancelled = "status.cancelled";
    public const string Awaiting = "status.awaiting";
    public const string Message = "chat.message";
    public const string Assistant = "chat.assistant";
    public const string Tool = "chat.tool";
    public const string Attachment = "chat.attachment";
}

/// <summary>Human and AI-readable icon metadata. SvgFile is relative to the selected icon pack.</summary>
public sealed record DesktopIconDefinition(string Key, string Label, string Description, string SvgFile);

/// <summary>A null directory denotes the embedded pack; Diagnostic describes any optional pack rejected during selection.</summary>
public sealed record DesktopIconPackStatus(string? Directory, string? Diagnostic);

/// <summary>
/// Shared native WPF icons with no SVG renderer or executable XAML. External INI packs are bounded data;
/// invalid optional packs degrade to the application pack or embedded geometry with a diagnostic.
/// </summary>
public static class DesktopIcons
{
    public const string PackRelativeDirectory = "Icons/chat-v1";
    private const string EmbeddedIndex = "Cerebral.Desktop.Plugins.ChatIcons.index.ini";
    private const int MaxIndexBytes = 128 * 1024;
    private static readonly string[] KnownKeys =
    [
        DesktopIconKeys.Success, DesktopIconKeys.Error, DesktopIconKeys.Warning, DesktopIconKeys.Working,
        DesktopIconKeys.Info, DesktopIconKeys.Cancelled, DesktopIconKeys.Awaiting, DesktopIconKeys.Message,
        DesktopIconKeys.Assistant, DesktopIconKeys.Tool, DesktopIconKeys.Attachment
    ];
    private sealed record Icon(DesktopIconDefinition Definition, Geometry Geometry);
    private sealed record Pack(IReadOnlyDictionary<string, Icon> Icons, DesktopIconPackStatus Status);
    private sealed record Cached(long Length, DateTime LastWrite, Pack? Pack, string? Diagnostic);
    private static readonly object Gate = new();
    private static readonly Dictionary<string, Cached> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Lazy<Pack> Embedded = new(() =>
    {
        using var stream = typeof(DesktopIcons).Assembly.GetManifestResourceStream(EmbeddedIndex)
            ?? throw new InvalidOperationException("The embedded chat icon index is missing.");
        return Parse(stream, new(null, null));
    });

    public static IReadOnlyList<DesktopIconDefinition> Definitions =>
        KnownKeys.Select(key => Embedded.Value.Icons[key].Definition).ToArray();

    public static DesktopIconPackStatus GetPackStatus(DesktopProductPaths? productPaths = null) =>
        SelectPack(productPaths).Status;

    /// <summary>
    /// Creates an unparented control on the calling WPF dispatcher. Geometry preserves its 20px design canvas;
    /// foreground follows the host's live TextBrush. Provide current ProductPaths to use configured shared assets.
    /// </summary>
    public static FrameworkElement Create(string key, double size = 16, DesktopProductPaths? productPaths = null)
    {
        if (!double.IsFinite(size) || size < 8 || size > 256)
            throw new ArgumentOutOfRangeException(nameof(size), "Icon size must be between 8 and 256.");
        if (key is null || !KnownKeys.Contains(key, StringComparer.Ordinal))
            throw new ArgumentException("Use a declared DesktopIconKeys identity.", nameof(key));
        var icon = SelectPack(productPaths).Icons[key];
        var path = new System.Windows.Shapes.Path { Data = icon.Geometry };
        var canvas = new Canvas { Width = 20, Height = 20 };
        canvas.Children.Add(path);
        var control = new ContentControl
        {
            Width = size, Height = size, Focusable = false, IsTabStop = false, Padding = new Thickness(0),
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Stretch,
            Content = new Viewbox { Child = canvas, Stretch = Stretch.Uniform },
            ToolTip = icon.Definition.Label
        };
        control.SetResourceReference(Control.ForegroundProperty, DesktopThemeResourceKeys.TextBrush);
        path.SetBinding(Shape.FillProperty, new Binding(nameof(Control.Foreground)) { Source = control });
        AutomationProperties.SetName(control, icon.Definition.Label);
        AutomationProperties.SetHelpText(control, icon.Definition.Description);
        return control;
    }

    private static Pack SelectPack(DesktopProductPaths? paths)
    {
        var roots = new List<string>();
        if (paths is not null)
        {
            AddRoot(roots, paths.SharedDirectory, PackRelativeDirectory);
            AddRoot(roots, paths.InstallDirectory, "Resources/" + PackRelativeDirectory);
        }
        AddRoot(roots, AppContext.BaseDirectory, "Resources/" + PackRelativeDirectory);
        var diagnostics = new List<string>();
        foreach (var root in roots.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var file = new FileInfo(System.IO.Path.Combine(root, "index.ini"));
                if (!file.Exists) continue;
                Pack? pack;
                string? diagnostic;
                lock (Gate)
                {
                    if (Cache.TryGetValue(file.FullName, out var cached) && cached.Length == file.Length &&
                        cached.LastWrite == file.LastWriteTimeUtc)
                    {
                        pack = cached.Pack; diagnostic = cached.Diagnostic;
                    }
                    else
                    {
                        try
                        {
                            if (file.Length > MaxIndexBytes) throw new FormatException("index.ini exceeds 128 KiB.");
                            using var stream = file.OpenRead();
                            pack = Parse(stream, new(root, null)); diagnostic = null;
                        }
                        catch (Exception error) when (error is IOException or UnauthorizedAccessException or FormatException or ArgumentException)
                        {
                            pack = null; diagnostic = $"Chat icon pack rejected at {root}: {error.Message}";
                        }
                        if (Cache.Count >= 8) Cache.Clear();
                        Cache[file.FullName] = new(file.Length, file.LastWriteTimeUtc, pack, diagnostic);
                    }
                }
                if (diagnostic is not null) diagnostics.Add(diagnostic);
                if (pack is not null) return pack with { Status = new(root, JoinDiagnostics(diagnostics)) };
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                diagnostics.Add($"Chat icon pack unavailable at {root}: {error.Message}");
            }
        }
        return Embedded.Value with { Status = new(null, JoinDiagnostics(diagnostics)) };
    }

    private static string? JoinDiagnostics(List<string> diagnostics) =>
        diagnostics.Count == 0 ? null : string.Join(Environment.NewLine, diagnostics);

    private static void AddRoot(List<string> roots, string directory, string relative)
    {
        if (!string.IsNullOrWhiteSpace(directory) && System.IO.Path.IsPathFullyQualified(directory))
            roots.Add(System.IO.Path.GetFullPath(System.IO.Path.Combine(directory, relative)));
    }

    private static Pack Parse(Stream stream, DesktopIconPackStatus status)
    {
        using var bounded = new MemoryStream();
        var buffer = new byte[4096];
        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) != 0)
        {
            if (bounded.Length + read > MaxIndexBytes) throw new FormatException("index.ini exceeds 128 KiB.");
            bounded.Write(buffer, 0, read);
        }
        var text = new UTF8Encoding(false, true).GetString(bounded.ToArray()).TrimStart('\uFEFF');
        var sections = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
        Dictionary<string, string>? section = null;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith(';')) continue;
            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                var name = line[1..^1];
                if (name != "pack" && !KnownKeys.Contains(name, StringComparer.Ordinal))
                    throw new FormatException("Unknown icon section.");
                section = new(StringComparer.Ordinal);
                if (!sections.TryAdd(name, section)) throw new FormatException("Duplicate icon section.");
                continue;
            }
            var equals = line.IndexOf('=');
            if (section is null || equals <= 0 || !section.TryAdd(line[..equals].Trim(), line[(equals + 1)..].Trim()))
                throw new FormatException("Invalid or duplicate INI property.");
        }
        if (!sections.TryGetValue("pack", out var header) || Required(header, "contract", 64) != "pegasus.cerebral.chat-icons/1" ||
            Required(header, "viewport", 8) != "20" || Required(header, "license", 32) != "MIT" ||
            sections.Count != KnownKeys.Length + 1)
            throw new FormatException("Unsupported or incomplete chat icon pack.");
        var icons = new Dictionary<string, Icon>(StringComparer.Ordinal);
        foreach (var key in KnownKeys)
        {
            if (!sections.TryGetValue(key, out var properties)) throw new FormatException($"Missing icon {key}.");
            var svg = Required(properties, "svg", 256);
            if (!svg.StartsWith("svg/", StringComparison.Ordinal) || !svg.EndsWith(".svg", StringComparison.Ordinal) ||
                svg.Split('/').Any(part => part is "" or "." or "..") || svg.Contains('\\') || svg.Contains(':'))
                throw new FormatException("SVG metadata must name a contained relative source asset.");
            var geometry = Geometry.Parse(Required(properties, "geometry", 8192));
            var bounds = geometry.Bounds;
            var area = geometry.GetArea();
            if (bounds.IsEmpty || !double.IsFinite(bounds.X) || !double.IsFinite(bounds.Y) ||
                !double.IsFinite(bounds.Width) || !double.IsFinite(bounds.Height) ||
                !double.IsFinite(area) || area <= 0 || bounds.Width <= 0 || bounds.Height <= 0 ||
                bounds.Left < 0 || bounds.Top < 0 || bounds.Right > 20 || bounds.Bottom > 20)
                throw new FormatException("Icon geometry must be nonempty and contained in the 20px canvas.");
            geometry.Freeze();
            icons.Add(key, new(new(key, Required(properties, "label", 128),
                Required(properties, "description", 512), svg), geometry));
        }
        return new(new ReadOnlyDictionary<string, Icon>(icons), status);
    }

    private static string Required(Dictionary<string, string> properties, string name, int maximum)
    {
        if (!properties.TryGetValue(name, out var value) || string.IsNullOrWhiteSpace(value) ||
            value.Length > maximum || value.Any(char.IsControl))
            throw new FormatException($"Invalid icon property: {name}.");
        return value;
    }
}
