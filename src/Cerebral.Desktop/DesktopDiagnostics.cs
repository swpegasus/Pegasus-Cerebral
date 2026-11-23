using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Automation;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Cerebral.Core;

namespace Cerebral.Desktop;

public static class DesktopDiagnostics
{
    public static int CheckService(Application app, ConnectedWindow window, string endpoint, string? sessionId)
    {
        app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        app.Startup += async (_, _) =>
        {
            var exitCode = 0;
            try
            {
                var loaded = await window.InspectServiceUiAsync(endpoint, sessionId);
                if (window.Content is FrameworkElement root) { root.Measure(new Size(1280, 840)); root.Arrange(new Rect(0, 0, 1280, 840)); root.UpdateLayout(); }
                Console.WriteLine(JsonSerializer.Serialize(new { success = true, application = window.Title, serviceCommandsExecuted = true, configurationReadsMayInitializeDefaults = true, loaded }));
            }
            catch (Exception ex) { exitCode = 1; Console.Error.WriteLine(JsonSerializer.Serialize(new { success = false, error = ex.ToString() })); }
            finally { window.Close(); app.Shutdown(exitCode); }
        };
        return app.Run();
    }
    /// <summary>Shows the complete desktop against one explicitly owned local Worker pipe. The caller
    /// starts and stops that Worker; this check never reads or changes the user's saved connection.</summary>
    public static int CheckPipeService(Application app, ConnectedWindow window, LocalWorkerEndpoint endpoint,
        string dataDirectory, string? sessionId, string? previewPath = null)
    {
        ArgumentNullException.ThrowIfNull(app); ArgumentNullException.ThrowIfNull(window); ArgumentNullException.ThrowIfNull(endpoint);
        endpoint.Validate();
        if (!Path.IsPathFullyQualified(dataDirectory)) throw new ArgumentException("Pipe UI inspection requires an absolute Worker data directory.", nameof(dataDirectory));
        app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        app.Startup += async (_, _) =>
        {
            var exitCode = 0;
            try
            {
                var loaded = await window.InspectServiceUiAsync(endpoint, dataDirectory, sessionId);
                window.Show();
                await window.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Render);
                window.UpdateLayout();
                if (!window.IsVisible || new WindowInteropHelper(window).Handle == nint.Zero || PresentationSource.FromVisual(window) is not HwndSource)
                    throw new InvalidOperationException("The complete Pegasus Cerebral window did not acquire a visible native window.");
                if (window.Content is not FrameworkElement root || root.ActualWidth < 700 || root.ActualHeight < 580)
                    throw new InvalidOperationException("The complete Pegasus Cerebral surface did not produce a usable layout.");
                var dpi = VisualTreeHelper.GetDpi(root);
                if (previewPath is not null) SaveShownContent(root, previewPath, dpi);
                Console.WriteLine(JsonSerializer.Serialize(new
                {
                    success = true, application = window.Title, windowShown = true, actualHwnd = true,
                    transport = "authenticated-local-pipe", serviceCommandsExecuted = true,
                    savedConnectionApplied = false, savedConnectionChanged = false,
                    clientWidth = root.ActualWidth, clientHeight = root.ActualHeight,
                    actualDpiX = dpi.PixelsPerInchX, actualDpiY = dpi.PixelsPerInchY,
                    previewPath, loaded
                }));
            }
            catch (Exception ex) { exitCode = 1; Console.Error.WriteLine(JsonSerializer.Serialize(new { success = false, error = ex.ToString() })); }
            finally { window.Close(); app.Shutdown(exitCode); }
        };
        return app.Run();
    }
    private static void SaveShownContent(FrameworkElement content, string path, DpiScale dpi)
    {
        if (!Path.IsPathFullyQualified(path) || !path.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The shown-window preview requires an absolute PNG path.", nameof(path));
        if (PresentationSource.FromVisual(content) is not HwndSource)
            throw new InvalidOperationException("Only shown native content can be captured.");
        var width = checked((int)Math.Ceiling(content.ActualWidth * dpi.DpiScaleX));
        var height = checked((int)Math.Ceiling(content.ActualHeight * dpi.DpiScaleY));
        if (width is < 1 or > 8192 || height is < 1 or > 8192)
            throw new InvalidOperationException("The native capture exceeds its supported dimensions.");
        var bitmap = new RenderTargetBitmap(width, height, dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        bitmap.Render(content);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        encoder.Save(file);
    }
    /// <summary>Constructs and measures the real WPF UI without opening a window or issuing service commands.</summary>
    public static int Check(Window window)
    {
        try
        {
            if (window.Content is not FrameworkElement root) throw new InvalidOperationException("Window has no layout root.");
            root.Measure(new Size(1280, 840)); root.Arrange(new Rect(0, 0, 1280, 840)); root.UpdateLayout();
            var preview = MarkdownView.Render("# Renderer check\nPlain **bold** and `code`.\n\n| One | Two |\n|---|---|\n| A | B |\n```csharp\nvar value = 42;\n```\n```diff\n-old\n+new\n```\n<script>text only</script>");
            if (preview is not RichTextBox { IsReadOnly: true, IsDocumentEnabled: true } selectable) throw new InvalidOperationException("Markdown must support native read-only text selection.");
            var selectedText = new System.Windows.Documents.TextRange(selectable.Document.ContentStart, selectable.Document.ContentEnd).Text;
            if (!selectedText.Contains("Renderer check") || !selectedText.Contains("var value = 42;") || !selectedText.Contains("-old")) throw new InvalidOperationException("Selectable document lost Markdown or code content.");
            var readOnlyMenu = selectable.ContextMenu?.Items.OfType<MenuItem>().Select(AutomationProperties.GetAutomationId).ToHashSet(StringComparer.Ordinal)
                ?? throw new InvalidOperationException("Rendered Markdown needs a context menu.");
            if (!readOnlyMenu.SetEquals(["TextContextCopy", "TextContextSelectAll"]))
                throw new InvalidOperationException("Read-only Markdown must offer Copy and Select all without editing actions.");
            var editor = Ui.Input("editable");
            var editingMenu = editor.ContextMenu?.Items.OfType<MenuItem>().Select(AutomationProperties.GetAutomationId).ToHashSet(StringComparer.Ordinal)
                ?? throw new InvalidOperationException("Editable text needs a context menu.");
            var expectedEditingActions = new[] { "TextContextUndo", "TextContextRedo", "TextContextCut", "TextContextCopy", "TextContextPaste", "TextContextDelete", "TextContextSelectAll" };
            if (!expectedEditingActions.All(editingMenu.Contains)) throw new InvalidOperationException("Editable text is missing ordinary Windows context actions.");
            preview.Measure(new Size(700, double.PositiveInfinity)); preview.Arrange(new Rect(0, 0, 700, preview.DesiredSize.Height));
            var connectionStoreChecks = ConnectionStoreChecks.Verify();
            var workAreaLayoutChecks = WindowSizingChecks.Verify(window);
            Console.WriteLine(JsonSerializer.Serialize(new { success = true, application = window.Title, layoutWidth = root.ActualWidth, layoutHeight = root.ActualHeight, rendererHeight = preview.ActualHeight, textContextMenuChecks = expectedEditingActions.Length + readOnlyMenu.Count, connectionStoreChecks, workAreaLayoutChecks, completeDeploymentLayout = ManagementProcess.UsesInstalledLayout, display = WindowSizing.Inspect(window), dpiSimulation = false, serviceCommandsExecuted = false }));
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(JsonSerializer.Serialize(new { success = false, error = ex.ToString() })); return 1;
        }
        finally { window.Close(); }
    }
}
