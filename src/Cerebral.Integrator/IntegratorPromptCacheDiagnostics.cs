using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Cerebral.Core;
using Cerebral.Desktop;

namespace Cerebral.Integrator;

public sealed partial class IntegratorWindow
{
    private readonly TextBlock promptCacheSummary = Ui.Text("Refresh to inspect optional cache measurements.", 13);
    private readonly DataGrid promptCacheHistory = Ui.Grid(
        ("Time", "Time", .9), ("Model / worker", "Model", 1.8), ("Full input", "Input", .8),
        ("Cached", "Cached", .8), ("Reuse", "Reuse", .7), ("Processed", "Processed", .8),
        ("Prefill", "Prefill", .8), ("Status", "Status", 1.8));

    private UIElement PromptCacheDiagnosticsCard()
    {
        var body = new StackPanel();
        body.Children.Add(Ui.Text("Prompt cache measurements", 18));
        body.Children.Add(Ui.Text("Optional numeric measurements, kept for a rolling 24 hours. Repeated readings are suppressed; availability changes or the configured change threshold create an entry. Full input tokens still count toward the context window. Cache memory is not estimated from token counts.", 13, Ui.Muted));
        body.Children.Add(promptCacheSummary);
        body.Children.Add(Ui.Bar(Ui.Button("Refresh cache diagnostics", () => Run(RefreshPromptCacheDiagnostics))));
        promptCacheHistory.MinHeight = 100;
        promptCacheHistory.MaxHeight = 280;
        promptCacheHistory.IsReadOnly = true;
        AutomationProperties.SetAutomationId(promptCacheHistory, "PromptCacheDiagnosticsHistory");
        body.Children.Add(promptCacheHistory);
        return body;
    }

    private async Task RefreshPromptCacheDiagnostics()
    {
        if (!IsConnected)
        {
            promptCacheSummary.Text = "Connect to a Worker to inspect cache diagnostics.";
            promptCacheHistory.ItemsSource = null;
            return;
        }
        var response = await Client.CommandAsync("diagnostics.prompt-cache.get", new { limit = 100 }, cancellationToken: Lifetime.Token);
        if (!response.Success)
        {
            // Optional older Workers can still generate normally without this diagnostics surface.
            var detail = response.Error ?? "No diagnostics data was returned.";
            promptCacheSummary.Text = "Cache diagnostics are unavailable from this Worker. Inference is unaffected. " +
                detail[..Math.Min(320, detail.Length)];
            promptCacheHistory.ItemsSource = null;
            return;
        }
        var snapshot = CerebralJson.Read<PromptCacheDiagnosticsSnapshot>(response.Data);
        promptCacheSummary.Text = $"Collection {(snapshot.Enabled ? "enabled" : "disabled")} · rolling {snapshot.RetentionHours} hours · at most {snapshot.MaximumEntries:N0} entries · reuse change {snapshot.ReuseChangePercentagePoints} percentage points; input/prefill change {snapshot.ReuseChangePercentagePoints}% (prefill floor 25 ms; input floor 128 tokens).";
        if (snapshot.Entries.Count == 0)
            promptCacheSummary.Text += " No measurements retained yet; unavailable values are shown as N/A.";
        promptCacheHistory.ItemsSource = snapshot.Entries.Select(entry => new
        {
            Time = entry.At.ToLocalTime().ToString("MMM d h:mm:ss tt"),
            Model = entry.ModelId + " / " + entry.WorkerId,
            Input = entry.PreparedInputTokens.ToString("N0"),
            Cached = entry.CachedTokens?.ToString("N0") ?? "N/A",
            Reuse = entry.ReuseRatio is { } ratio ? ratio.ToString("P1") : "N/A",
            Processed = entry.ProcessedPromptTokens?.ToString("N0") ?? "N/A",
            Prefill = entry.PromptProcessingMilliseconds is { } milliseconds ? milliseconds.ToString("N1") + " ms" : "N/A",
            Status = entry.State == "reported" ? "Provider measured" : entry.Diagnostic == "invalid-provider-telemetry"
                ? "Invalid provider metrics" : "Provider did not report metrics"
        }).ToArray();
    }

    /// <summary>Real owner-pipe read and native WPF rendering; no foreground window, settings write or model call.</summary>
    internal async Task CheckPromptCachePageAsync(string capturePath)
    {
        Dispatcher.VerifyAccess();
        var saved = ConnectionStore.Read().Preferences;
        if (saved?.Transport != "pipe" || saved.LocalEndpoint is null || string.IsNullOrWhiteSpace(saved.LocalDataDirectory))
            throw new InvalidOperationException("Cache diagnostics inspection requires an existing local owner pipe.");
        await InspectServiceUiAsync(saved.LocalEndpoint, saved.LocalDataDirectory, null);
        if (!SelectTab(Tabs, "Retention"))
            throw new InvalidOperationException("The history and cache settings page is unavailable.");
        await RefreshPromptCacheDiagnostics();
        if (!IsConnected || promptCacheSummary.Text.StartsWith("Cache diagnostics are unavailable", StringComparison.Ordinal))
            throw new InvalidOperationException("The owner did not return cache diagnostics.");
        var layout = (FrameworkElement)Content;
        layout.Measure(new Size(1360, 880)); layout.Arrange(new Rect(0, 0, 1360, 880)); layout.UpdateLayout();
        // Hidden windows do not process BringIntoView the same way as a presented Window.
        // Drive the real page scroller, then verify the card is actually within its viewport.
        var targetOffset = retentionContentScroller.VerticalOffset + promptCacheSummary.TranslatePoint(new Point(), retentionContentScroller).Y - 70;
        retentionContentScroller.ScrollToVerticalOffset(Math.Max(0, targetOffset));
        await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Loaded);
        layout.UpdateLayout();
        var position = promptCacheHistory.TranslatePoint(new Point(), retentionContentScroller);
        if (promptCacheHistory.ActualWidth <= 0 || promptCacheHistory.ActualHeight <= 0 || position.Y < 0 ||
            position.Y + promptCacheHistory.ActualHeight > retentionContentScroller.ViewportHeight)
            throw new InvalidOperationException("Cache diagnostics did not obtain a visible native layout.");
        var image = new RenderTargetBitmap(1360, 880, 96, 96, PixelFormats.Pbgra32);
        image.Render(layout);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
        using (var file = new FileStream(Path.GetFullPath(capturePath), FileMode.CreateNew, FileAccess.Write)) encoder.Save(file);
        Console.WriteLine(JsonSerializer.Serialize(new { success = true, connected = IsConnected,
            summary = promptCacheSummary.Text, rows = promptCacheHistory.Items.Count, windowShown = false,
            modelCallsExecuted = false, settingsWritten = false, capture = Path.GetFullPath(capturePath) }));
    }
}
