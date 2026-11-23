using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Cerebral.Desktop.Plugins;
using Cerebral.Orchestrator;

namespace Cerebral.Orchestrator.StatusIcons.Tests;

public static class Program
{
    private static int checks;
    private const string Prefix = "Cerebral.Orchestrator.StatusIcons.";
    private static readonly (StatusMetricIcon Key, string File, string Sha)[] Icons =
    [
        (StatusMetricIcon.Cpu, "cpu-line.svg", "c3f9d4128b86b8bb8028f33f125f45e7f3abc643729fd70fc63b46ced91b8629"),
        (StatusMetricIcon.Ram, "ram-2-line.svg", "342f1f3ac3ef036c13697a24fb1b6886e94ca6c7f9cf9801b3029ceda4e5d7ed"),
        (StatusMetricIcon.Storage, "hard-drive-2-line.svg", "8ef731c6702a19db270c82607d39138293b026b8a3127512876de0d46819df2d"),
        (StatusMetricIcon.Machine, "computer-line.svg", "41ce0e2a6928afe4e627524807603cce9021c560ed1e39ec3d5dbfb55f6471b4"),
        (StatusMetricIcon.Model, "robot-2-line.svg", "7c6b289f2f5ebe53b38c3862d0325d720621ae044a58a1b06691808d35f80807"),
        (StatusMetricIcon.Queue, "stack-line.svg", "c3d706391b37a7ce18d0b17e04cd524c37bfb58c806493e191e7dc3c95cda393")
    ];

    [STAThread]
    public static int Main(string[] args)
    {
        try
        {
            RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly;
            _ = new Application();
            Application.Current.Resources[DesktopThemeResourceKeys.TextBrush] = Brushes.Black;
            VerifyEmbeddedSources();
            VerifyAdmissionAndRendering();
            RejectUnsupportedArtwork();
            VerifyMetricUpdatesAndFallback();
            VerifyNarrowControlsStayClear();
            if (args is [var preview]) SavePreview(Path.GetFullPath(preview));
            else if (args.Length != 0) throw new ArgumentException("Only an optional absolute fixture PNG path is supported.");
            Console.WriteLine($"Status icons: {checks} deterministic checks passed; passive embedded resources and offscreen software WPF only, no live app, IPC, model, network or deployment.");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }

    private static void VerifyEmbeddedSources()
    {
        using var provenance = JsonDocument.Parse(Bytes("provenance.json"));
        Check(provenance.RootElement.GetProperty("sourceContract").GetString() == "pegasus.assets/2" &&
              provenance.RootElement.GetProperty("sourceNamespace").GetString() == "knowledge.shared" &&
              provenance.RootElement.GetProperty("upstreamRevision").GetString() == "4ef50878f17accbed89ef2691bdc357562ed0a17",
            "embedded provenance retains the exact native Cortex namespace and upstream snapshot");
        var assets = provenance.RootElement.GetProperty("assets").EnumerateArray().ToArray();
        Check(assets.Length == 8, "only the six selected originals and their two licensing bodies are retained");
        foreach (var asset in assets)
        {
            var bytes = Bytes(asset.GetProperty("file").GetString()!);
            Check(bytes.Length == asset.GetProperty("size").GetInt32() &&
                  Digest(bytes) == asset.GetProperty("sha256").GetString(),
                "embedded complete original matches verified provenance: " + asset.GetProperty("file").GetString());
        }
        Check(Digest(Bytes("LICENSE.txt")) == "c71d239df91726fc519c6eb72d318ec65820627232b2f796219e87dcf35d0ab4" &&
              Digest(Bytes("NOTICE.txt")) == "e2bc5c5944a98713a617159ddab04ace9c243d1e26719abd5d5a808a81c07861",
            "exact selected Apache license and snapshot notice remain present");
    }

    private static void VerifyAdmissionAndRendering()
    {
        foreach (var icon in Icons)
        {
            var admitted = StatusMetricIcons.Admit(Bytes(icon.File), icon.Sha);
            Check(admitted.IsFrozen && admitted is StreamGeometry { FillRule: FillRule.Nonzero },
                icon.Key + " preserves SVG nonzero winding and freezes its admitted geometry");
            var first = (ContentControl)StatusMetricIcons.Create(icon.Key, icon.Key.ToString())!;
            var second = (ContentControl)StatusMetricIcons.Create(icon.Key, icon.Key.ToString())!;
            Check(first.Parent is null && second.Parent is null && !ReferenceEquals(first, second) &&
                  ReferenceEquals(IconPath(first).Data, IconPath(second).Data),
                icon.Key + " creates unparented views while reusing the fixed cached geometry");
            first.Resources[DesktopThemeResourceKeys.TextBrush] = Brushes.White;
            Check(first.Foreground == Brushes.White && IconPath(first).Fill == Brushes.White,
                icon.Key + " follows a live light theme tint");
            first.Resources[DesktopThemeResourceKeys.TextBrush] = Brushes.Black;
            Check(first.Foreground == Brushes.Black && IconPath(first).Fill == Brushes.Black &&
                  AutomationProperties.GetName(first) == icon.Key.ToString(),
                icon.Key + " follows a live dark tint and retains its accessible semantic label");
            var bitmap = Render(first, 18, 18);
            var pixels = new byte[18 * 18 * 4];
            bitmap.CopyPixels(pixels, 18 * 4, 0);
            Check(Enumerable.Range(0, 18 * 18).Count(index => pixels[index * 4 + 3] > 0) > 5,
                icon.Key + " produces visible pixels with the offscreen software renderer");
        }
        Check(StatusMetricIcons.Diagnostic is null, "all selected embedded originals render without a fallback diagnostic");
        var missing = StatusMetricIcons.Load(_ => null);
        Check(missing.Geometries.Count == 0 && missing.Diagnostic?.Contains("labels retained") == true,
            "missing resources produce a bounded diagnostic and empty icon pack without a startup exception");
        var corrupt = StatusMetricIcons.Load(_ => new MemoryStream("corrupt"u8.ToArray()));
        Check(corrupt.Geometries.Count == 0 && corrupt.Diagnostic?.Contains("SHA-256") == true,
            "corrupt resource admission degrades the entire fixed pack without publishing partial artwork");
    }

    private static void RejectUnsupportedArtwork()
    {
        const string open = "<svg viewBox=\"0 0 24 24\" fill=\"currentColor\" xmlns=\"http://www.w3.org/2000/svg\">";
        const string path = "<path d=\"M2 2H22V22H2Z\"/>";
        foreach (var (label, text) in new[]
        {
            ("DTD", "<!DOCTYPE svg [<!ENTITY x SYSTEM \"file:///unrelated\">]>" + open + path + "</svg>"),
            ("script", open + "<script>alert(1)</script>" + path + "</svg>"),
            ("processing instruction", "<?xml-stylesheet href=\"https://example.invalid/a.css\"?>" + open + path + "</svg>"),
            ("external image", open + "<image href=\"https://example.invalid/a.svg\"/>" + path + "</svg>"),
            ("event handler", open.Replace("fill=\"currentColor\"", "fill=\"currentColor\" onload=\"active()\"") + path + "</svg>"),
            ("external paint", open.Replace("currentColor", "url(https://example.invalid/paint)") + path + "</svg>"),
            ("transform", open + "<path transform=\"scale(2)\" d=\"M2 2H22V22H2Z\"/></svg>"),
            ("foreign namespace", open + "<path xmlns=\"urn:unsupported\" d=\"M2 2H22V22H2Z\"/></svg>"),
            ("extra path", open + path + path + "</svg>"),
            ("empty path", open + "<path d=\"\"/></svg>"),
            ("unsupported command", open + "<path d=\"M2 2Q12 22 22 2Z\"/></svg>"),
            ("escaping canvas", open + "<path d=\"M2 2H25V22H2Z\"/></svg>"),
            ("zero area", open + "<path d=\"M2 2H22Z\"/></svg>"),
            ("oversize", open + path + new string(' ', 4096) + "</svg>")
        })
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            Reject(() => StatusMetricIcons.Admit(bytes, Digest(bytes)), label + " is rejected even with a matching byte hash");
        }
        Reject(() => StatusMetricIcons.Admit(Bytes("cpu-line.svg"), new string('0', 64)),
            "an original with the wrong full digest is rejected before rendering");
    }

    private static void VerifyMetricUpdatesAndFallback()
    {
        var target = Text();
        var calls = 0;
        var line = new StatusMetricLine(target,
            [(null, ""), (StatusMetricIcon.Cpu, "Cerebral CPU"), (StatusMetricIcon.Ram, "RAM"), (StatusMetricIcon.Queue, "Queued")],
            (key, label) => { calls++; return StatusMetricIcons.Create(key, label); });
        line.SetValues(["GPU —", "12.5%", "2.0 GiB", "3"], "fixture details");
        var spans = target.Inlines.OfType<Span>().ToArray();
        var controls = spans.SelectMany(span => span.Inlines.OfType<InlineUIContainer>()).Select(inline => inline.Child).ToArray();
        for (var index = 0; index < 100; index++)
            line.SetValues(["GPU —", "13%", "2.1 GiB", index.ToString()], "updated details");
        Check(calls == 3 && target.Inlines.OfType<Span>().SequenceEqual(spans) &&
              spans.SelectMany(span => span.Inlines.OfType<InlineUIContainer>()).Select(inline => inline.Child).SequenceEqual(controls),
            "ordinary refreshes reuse a fixed number of inline groups and views without recreating artwork");
        Check(AutomationProperties.GetName(target).Contains("Cerebral CPU 13%") &&
              AutomationProperties.GetName(target).Contains("Queued 99") &&
              target.ToolTip!.ToString()!.Contains("updated details") && target.ToolTip.ToString()!.Contains("Queued 99"),
            "compact icon values preserve complete metric names in accessible status and detail");
        line.ShowMessage("metrics unavailable", "fixture offline");
        Check(target.Text == "metrics unavailable" && target.ToolTip!.ToString() == "fixture offline",
            "loss of service shows readable unavailable text instead of stale icon values");
        line.SetValues(["GPU —", "—", "2.1 GiB", "0"], "restored");
        Check(target.Inlines.OfType<Span>().SequenceEqual(spans) && calls == 3,
            "status restoration reuses the same groups after an unavailable message");
        Reject(() => line.SetValues(["mismatch"], "invalid"), "a mismatched internal projection rejects without guessing values");
        var fallback = Text();
        var missing = new StatusMetricLine(fallback, [(StatusMetricIcon.Cpu, "Cerebral CPU"), (StatusMetricIcon.Queue, "Queued")],
            (_, _) => null);
        missing.SetValues(["—", "4"], "fixture labels only");
        Check(new TextRange(fallback.ContentStart, fallback.ContentEnd).Text.Contains("Cerebral CPU —") &&
              new TextRange(fallback.ContentStart, fallback.ContentEnd).Text.Contains("Queued 4"),
            "rendering fallback retains readable metric labels and values");
        target.Measure(new Size(100, double.PositiveInfinity));
        Check(target.DesiredSize.Height < 32,
            "the icon metric line stays on one row at a narrow available width");
    }

    private static void VerifyNarrowControlsStayClear()
    {
        var metrics = Text();
        metrics.Foreground = Brushes.Red;
        metrics.Resources[DesktopThemeResourceKeys.TextBrush] = Brushes.Red;
        new StatusMetricLine(metrics,
            [(null, ""), (StatusMetricIcon.Cpu, "CPU"), (StatusMetricIcon.Ram, "RAM"), (StatusMetricIcon.Storage, "Storage")])
            .SetValues(["GPU 9999%  VRAM 100.0/100.0 GiB", "123.0%", "100.0 GiB", "9999.0 GiB free"], "fixture wide values");
        var tokens = Text();
        tokens.Text = "1234 in / 5678 out";
        tokens.Foreground = Brushes.Blue;
        tokens.Margin = new Thickness(12, 0, 0, 0);
        var gpu = Text();
        gpu.Text = "Local GPU inactive";
        gpu.Foreground = Brushes.Green;
        gpu.Margin = new Thickness(12, 0, 0, 0);
        var dock = new DockPanel { Width = 360 };
        DockPanel.SetDock(gpu, Dock.Right);
        DockPanel.SetDock(tokens, Dock.Right);
        dock.Children.Add(gpu);
        dock.Children.Add(tokens);
        dock.Children.Add(StatusMetricLine.CreateClippedView(metrics));
        metrics.Visibility = Visibility.Hidden;
        var baseline = Render(dock, 360, 30);
        var start = Math.Clamp((int)Math.Floor(tokens.TranslatePoint(new Point(0, 0), dock).X), 0, 359);
        metrics.Visibility = Visibility.Visible;
        var visible = Render(dock, 360, 30);
        var stride = (360 - start) * 4;
        var before = new byte[stride * 30];
        var after = new byte[stride * 30];
        var region = new Int32Rect(start, 0, 360 - start, 30);
        baseline.CopyPixels(region, before, stride, 0);
        visible.CopyPixels(region, after, stride, 0);
        Check(before.SequenceEqual(after),
            "wide inline icon values cannot paint into the reserved token and GPU region at narrow width");
    }

    private static void SavePreview(string path)
    {
        if (!Path.IsPathFullyQualified(path)) throw new ArgumentException("The fixture preview requires an absolute output path.");
        var root = new StackPanel();
        foreach (var dark in new[] { false, true })
        {
            var panel = new StackPanel { Margin = new Thickness(12) };
            panel.Resources[DesktopThemeResourceKeys.TextBrush] = dark ? Brushes.White : Brushes.Black;
            var icons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 12) };
            foreach (var item in Icons)
            {
                var group = new StackPanel { Width = 112 };
                var icon = StatusMetricIcons.Create(item.Key, item.Key.ToString())!;
                icon.Width = icon.Height = 24;
                group.Children.Add(icon);
                group.Children.Add(new TextBlock
                {
                    Text = item.Key.ToString(), FontSize = 11, HorizontalAlignment = HorizontalAlignment.Center,
                    Foreground = dark ? Brushes.White : Brushes.Black
                });
                icons.Children.Add(group);
            }
            panel.Children.Add(icons);
            foreach (var width in new[] { 880d, 520d })
            {
                var metrics = Text();
                metrics.Foreground = dark ? Brushes.White : Brushes.Black;
                var tokens = Text();
                tokens.Foreground = metrics.Foreground;
                tokens.Margin = new Thickness(12, 0, 0, 0);
                new StatusMetricLine(metrics,
                    [(null, ""), (null, ""), (StatusMetricIcon.Cpu, "Cerebral CPU"), (StatusMetricIcon.Ram, "RAM"),
                     (StatusMetricIcon.Storage, "Storage"), (null, ""), (StatusMetricIcon.Queue, "Queued")])
                    .SetValues(["GPU —", "VRAM —", "12.5%", "2.0 GiB", "150.0 GiB free", "Active 0", "3"], "fixture metrics");
                new StatusMetricLine(tokens, [(StatusMetricIcon.Model, "Tokens")])
                    .SetValues(["1.2K in / 480 out"], "fixture confirmed model tokens");
                var gpu = new TextBlock
                {
                    Text = "● Local GPU inactive", Foreground = dark ? Brushes.LightGreen : Brushes.Green,
                    FontSize = 11, Margin = new Thickness(10, 0, 0, 0)
                };
                DockPanel.SetDock(gpu, Dock.Right);
                DockPanel.SetDock(tokens, Dock.Right);
                var dock = new DockPanel { Width = width, ClipToBounds = true };
                dock.Children.Add(gpu);
                dock.Children.Add(tokens);
                dock.Children.Add(StatusMetricLine.CreateClippedView(metrics));
                panel.Children.Add(new Border { Child = dock, Padding = new Thickness(8), Margin = new Thickness(0, 0, 0, 8) });
            }
            root.Children.Add(new Border { Child = panel, Background = dark ? Brushes.DimGray : Brushes.White });
        }
        var bitmap = Render(root, 920, 300);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var output = File.Create(path);
        encoder.Save(output);
        Check(File.Exists(path) && new FileInfo(path).Length > 0, "offscreen light/dark and narrow/wide fixture preview was saved");
        Console.WriteLine("Fixture preview: " + path);
    }

    private static TextBlock Text() => new()
    {
        FontSize = 11, TextWrapping = TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis,
        VerticalAlignment = VerticalAlignment.Center
    };
    private static System.Windows.Shapes.Path IconPath(ContentControl control) =>
        (System.Windows.Shapes.Path)((Canvas)((Viewbox)control.Content).Child).Children[0];
    private static RenderTargetBitmap Render(FrameworkElement element, int width, int height)
    {
        element.Measure(new Size(width, height));
        element.Arrange(new Rect(0, 0, width, height));
        element.UpdateLayout();
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(element);
        return bitmap;
    }
    private static byte[] Bytes(string file)
    {
        using var stream = typeof(Program).Assembly.GetManifestResourceStream(Prefix + file)
            ?? throw new IOException("Missing fixture resource: " + file);
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }
    private static string Digest(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    private static void Check(bool condition, string label)
    {
        if (!condition) throw new Exception("FAIL: " + label);
        checks++;
        Console.WriteLine("PASS: " + label);
    }
    private static void Reject(Action action, string label)
    {
        try { action(); }
        catch (Exception error) when (error is IOException or InvalidDataException or System.Xml.XmlException or ArgumentException or FormatException)
        { Check(true, label); return; }
        throw new Exception("FAIL: accepted " + label);
    }
}
