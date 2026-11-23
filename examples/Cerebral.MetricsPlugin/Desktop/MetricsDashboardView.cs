using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using Cerebral.Desktop.Plugins;

namespace Cerebral.MetricsPlugin.Desktop;

/// <summary>Native owner view. Pure rendering accepts recorded DTOs; no collector, model,
/// slot or process control is attached to this view's lifetime.</summary>
public sealed class MetricsDashboardView : UserControl, IDisposable
{
    private readonly StackPanel content = new() { Margin = new Thickness(18) };
    private readonly StackPanel results = new();
    private readonly TextBlock notice = Text("Connect to the metrics service to read collection status.", 13);
    private readonly TextBlock actionStatus = Text("", 12);
    private readonly TextBox model = Filter("Metrics.Filter.Model"), agent = Filter("Metrics.Filter.Agent"),
        chat = Filter("Metrics.Filter.Chat"), project = Filter("Metrics.Filter.Project"), worker = Filter("Metrics.Filter.Worker");
    private readonly ComboBox period = new() { MinWidth = 92, Height = 30, ItemsSource = new[] { "7 days", "30 days" }, SelectedIndex = 0 };
    private readonly ComboBox origin = new() { MinWidth = 110, Height = 30, ItemsSource = new[] { "All routes", "Local", "Remote", "Unknown" }, SelectedIndex = 0 };
    private readonly CheckBox capture = new() { Content = "Collect scalar history", Margin = new Thickness(0, 10, 18, 6) };
    private readonly TextBox retention = new() { Text = "30", Width = 60, Height = 29 };
    private readonly TextBox quota = new() { Text = "64", Width = 65, Height = 29 };
    private readonly Func<MetricsQuery, CancellationToken, Task<(MetricsStatus, MetricsQueryResult)>>? load;
    private readonly Func<MetricsSettingsChange, CancellationToken, Task<MetricsSettings>>? save;
    private readonly CancellationTokenSource lifetime = new();
    private readonly Button refresh = new() { Content = "Refresh", Padding = new Thickness(16, 5, 16, 5), MinWidth = 90 };
    private readonly Button saveButton = new() { Content = "Save collection settings", Padding = new Thickness(12, 5, 12, 5), Margin = new Thickness(0, 8, 0, 0) };
    private MetricsSettings? settings;
    private bool busy, disposed;

    internal MetricsDashboardView(Func<MetricsQuery, CancellationToken, Task<(MetricsStatus, MetricsQueryResult)>>? load = null,
        Func<MetricsSettingsChange, CancellationToken, Task<MetricsSettings>>? save = null)
    {
        this.load = load; this.save = save; FontFamily = new FontFamily("Segoe UI"); FontSize = 13;
        Foreground = new SolidColorBrush(Color.FromRgb(28, 44, 67));
        SetResourceReference(ForegroundProperty, DesktopThemeResourceKeys.TextBrush);
        Content = new ScrollViewer { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        AutomationProperties.SetAutomationId(this, "Metrics.Dashboard");
        content.Children.Add(Text("Metrics", 26, FontWeights.SemiBold));
        content.Children.Add(Paragraph("Inference and activity recorded on this Cerebral installation. Local and remote observations retain their own source, time and coverage."));
        notice.Margin = new Thickness(0, 12, 0, 12); notice.TextWrapping = TextWrapping.Wrap; content.Children.Add(Card(notice));
        var filters = new WrapPanel { Margin = new Thickness(0, 12, 0, 4) };
        filters.Children.Add(Labeled("Time range", period)); filters.Children.Add(Labeled("Worker scope", origin));
        content.Children.Add(filters);
        var advanced = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        advanced.Children.Add(Labeled("Model ID", model)); advanced.Children.Add(Labeled("Agent ID", agent));
        advanced.Children.Add(Labeled("Chat ID", chat)); advanced.Children.Add(Labeled("Project ID", project));
        advanced.Children.Add(Labeled("Worker ID", worker));
        content.Children.Add(new Expander { Header = "Advanced filters", Content = advanced, Margin = new Thickness(0, 2, 0, 8) });
        refresh.HorizontalAlignment = HorizontalAlignment.Left; refresh.Margin = new Thickness(0, 5, 0, 4);
        refresh.Click += async (_, _) => await RefreshAsync();
        AutomationProperties.SetAutomationId(refresh, "Metrics.Refresh"); content.Children.Add(refresh);
        content.Children.Add(actionStatus); content.Children.Add(results);
        content.Children.Add(Heading("Collection & storage"));
        var options = new WrapPanel(); options.Children.Add(capture);
        options.Children.Add(Labeled("Retention days", retention)); options.Children.Add(Labeled("Storage MiB", quota)); content.Children.Add(options);
        content.Children.Add(Paragraph("Retention defaults to 30 days or 64 MiB, whichever is reached first. These are storage limits, not model, context or inference limits. Capture is off until you enable it."));
        saveButton.HorizontalAlignment = HorizontalAlignment.Left; saveButton.IsEnabled = false;
        saveButton.Click += async (_, _) => await SaveAsync(); AutomationProperties.SetAutomationId(saveButton, "Metrics.SaveSettings"); content.Children.Add(saveButton);
        content.Children.Add(Heading("How to read these metrics"));
        content.Children.Add(Paragraph("Allocated slots describe configured capacity. Observed executions include host work; observed inference activity describes sampled model work. None reserves a slot or proves a hardware maximum."));
        content.Children.Add(Paragraph("Quarantined slots are withheld from reuse because the runtime has not confirmed safe release or cleanup. They are not available slots. This dashboard cannot clear quarantine or change resources."));
        content.Children.Add(Paragraph("Provider prompt/output phase timing, generation host time and whole-workflow time are different measurements. Missing data stays unavailable. Prompt text, responses, reasoning, tool bodies and credentials are excluded."));
        Loaded += OnLoaded;
    }
    private async void OnLoaded(object sender, RoutedEventArgs args) { if (load is not null) await RefreshAsync(); }
    internal async Task RefreshAsync()
    {
        if (busy || disposed || load is null) return;
        try
        {
            busy = true; refresh.IsEnabled = false; actionStatus.Text = "Reading bounded history…";
            var now = DateTimeOffset.UtcNow;
            var query = new MetricsQuery(now.AddDays(period.SelectedIndex == 0 ? -7 : -30), now.AddSeconds(1),
                Id(model), Id(agent), Id(chat), Id(project), origin.SelectedIndex switch { 1 => MetricOrigin.Local, 2 => MetricOrigin.Remote, 3 => MetricOrigin.Unknown, _ => null }, WorkerId: Id(worker));
            MetricsValidation.Query(query);
            var (status, result) = await load(query, lifetime.Token);
            if (!disposed) { Present(status, result); actionStatus.Text = "Updated " + Local(result.GeneratedAt) + ". Trend dates use UTC."; }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (OperationCanceledException)
        { if (!disposed) actionStatus.Text = "The metrics read was cancelled by the connection. Existing rows remain historical; refresh when connected."; }
        catch (Exception e) when (e is InvalidOperationException or ArgumentException or System.IO.IOException or System.Text.Json.JsonException)
        { if (!disposed) { notice.Text = "Metrics unavailable. Reconnect or install the matching service component, then refresh. No values are assumed."; actionStatus.Text = "The read did not complete. Existing displayed rows are historical; refresh before relying on them."; } }
        finally { busy = false; if (!disposed) refresh.IsEnabled = true; }
    }
    internal async Task SaveAsync()
    {
        if (disposed || busy || save is null || settings is null) return;
        try
        {
            if (!int.TryParse(retention.Text, out var days) || !long.TryParse(quota.Text, out var mib)) throw new ArgumentException();
            var preferences = new MetricsPreferences(capture.IsChecked == true, days, checked(mib * 1024 * 1024));
            MetricsValidation.Preferences(preferences); busy = true; saveButton.IsEnabled = false;
            settings = await save(new(settings.Revision, preferences), lifetime.Token);
            actionStatus.Text = "Collection settings saved. Refresh to inspect actual capture and storage state.";
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (OperationCanceledException)
        { if (!disposed) actionStatus.Text = "The settings result was not confirmed because the connection cancelled. Refresh before another save."; }
        catch (Exception e) when (e is InvalidOperationException or ArgumentException or System.IO.IOException or OverflowException or System.Text.Json.JsonException)
        { actionStatus.Text = "Settings were not confirmed. Refresh their current revision before another save. Retention must be 1–365 days and storage 1–256 MiB."; }
        finally { busy = false; if (!disposed) saveButton.IsEnabled = settings is not null; }
    }

    internal void Present(MetricsStatus status, MetricsQueryResult result)
    {
        settings = status.Settings; capture.IsChecked = settings.Values.CaptureEnabled;
        retention.Text = settings.Values.RetentionDays.ToString(CultureInfo.InvariantCulture);
        quota.Text = (settings.Values.MaximumBytes / 1024 / 1024).ToString(CultureInfo.InvariantCulture);
        saveButton.IsEnabled = save is not null;
        var coverage = result.Coverage;
        var source = coverage.State == "read-only" ? "History is read-only" : coverage.State == "limited" ? "History recording is blocked by its storage budget" :
            status.SourceState == "available" ? (status.CaptureActive ? "Collecting" : "Collection stopped") : "Runtime source unavailable";
        notice.Text = source + " · " + (status.Settings.Values.CaptureEnabled ? "capture requested" : "capture off") + "\n" +
            (status.SourceState == "available" ? "Numeric observations only. " : status.SourceReason == "reviewed-metrics-host-facet-not-connected"
                ? "The reviewed host metrics connection is not installed in this candidate. " : "The runtime source is not currently delivering observations. ") +
            (coverage.Earliest is { } earliest ? "Retained coverage: " + Local(earliest) + " – " + Local(coverage.Latest!.Value) + "." : "No recorded history. Unavailable does not mean zero.") +
            (coverage.Partial ? " Coverage is partial; gaps or omissions are retained." : "") +
            (coverage.Reason is not null ? " Storage: " + coverage.State + " (" + coverage.Reason + ")." : "");
        results.Children.Clear();
        var cards = new WrapPanel { Margin = new Thickness(0, 14, 0, 6) };
        AddCard(cards, "Confirmed input tokens", Total(result.Summary.InputTokens), "Receipt/usage counts only; unavailable for progress-only feeds");
        AddCard(cards, "Confirmed output tokens", Total(result.Summary.OutputTokens), "Receipt/usage counts; provider scope remains explicit");
        AddCard(cards, "Provider-observed input", Total(result.Summary.ProviderObservedInputTokens), "Progress lower bounds; not confirmed or billed usage");
        AddCard(cards, "Provider-observed output", Total(result.Summary.ProviderObservedOutputTokens), "Latest per-generation progress; not a visible-token total");
        AddCard(cards, "Completed workflows", Count(result.Summary.CompletedWorkflows, result.Summary.ObservedWorkflows),
            result.Summary.ObservedWorkflows == 0 ? "No recorded workflow outcomes" : "Failed: " + result.Summary.FailedWorkflows + " · Cancelled: " + result.Summary.CancelledWorkflows);
        AddCard(cards, "Provider output sample", result.Summary.LatestOutputTokensPerSecond is { } rate ? rate.ToString("N1", CultureInfo.CurrentCulture) + " tok/s" : "Unavailable", "Latest retained phase sample; not a benchmark");
        results.Children.Add(cards);
        results.Children.Add(Paragraph("Scope for confirmed counts: " + (result.Summary.OutputTokenScopes.Length == 0 ? "unavailable" : string.Join(", ", result.Summary.OutputTokenScopes)) +
            ". Basis for confirmed counts: " + (result.Summary.TokenBases.Length == 0 ? "unavailable" : string.Join(", ", result.Summary.TokenBases)) +
            ". Confirmed counts and provider progress are separate and are never added together."));
        results.Children.Add(Paragraph("The scalar source covers instrumented generations only. Workflow/tool outcomes, chat/project/agent attribution, context/cache budgets, routing cost and local/remote capacity require their own qualified sources; unavailable values are not zero. Unknown target origin stays separate."));
        results.Children.Add(Heading("Confirmed token trend"));
        results.Children.Add(Paragraph("Each generation's latest cumulative count is attributed to its observation date (UTC), including a run crossing midnight. This is not measured token consumption per day. Gaps are not zero-use days; rereads do not add another generation."));
        results.Children.Add(Card(new TokenTrend(result.Trends) { Height = 190, MinWidth = 260 }));
        results.Children.Add(Heading("Provider-observed output trend"));
        results.Children.Add(Paragraph("Progress lower bounds use each generation’s latest retained revision and UTC observation date. Missing revisions, disabled collection and source gaps prevent a complete consumption claim."));
        results.Children.Add(Card(new TokenTrend(result.Trends, providerObserved: true) { Height = 190, MinWidth = 260 }));
        results.Children.Add(Heading("Capacity observations"));
        var capacity = result.Capacity;
        if (capacity.Length == 0) results.Children.Add(Paragraph("Capacity unavailable. No local or remote capacity is inferred from another worker's state."));
        if (result.OmittedCapacity > 0) results.Children.Add(Paragraph($"Showing {capacity.Length} latest worker/model pool observations. {result.OmittedCapacity} additional pools were omitted by the bounded query; narrow the worker/model filters to inspect them."));
        foreach (var row in capacity)
        {
            var panel = Card(Paragraph($"{row.Origin} · {row.WorkerId ?? "worker unavailable"} · {row.ModelId ?? "model unavailable"}\n" +
                $"Allocated {Number(row.AllocatedSlots)}   Available {Number(row.AvailableSlots)}   Quarantined {Number(row.QuarantinedSlots)}\n" +
                $"Observed executions {Number(row.ObservedExecutions)}   Inference activity {Number(row.ObservedInferenceActivity)}\n" +
                $"Connection latency (measured RTT): {row.ConnectionLatencyMilliseconds?.ToString("N1") ?? "unavailable"} ms · Other routing/inference cost unavailable\n" +
                $"Historical sample · {Local(row.ObservedAt)} · {row.Basis}"));
            panel.ToolTip = "Observed by " + row.InstallationId + "; source generation " + row.SourceGeneration;
            results.Children.Add(panel);
        }
        results.Children.Add(Heading("Inference details"));
        var details = result.Rows.Where(row => row.Kind == MetricKind.Generation).Take(3).ToArray();
        if (details.Length == 0) results.Children.Add(Paragraph("Context, cache and phase measurements are unavailable in this range."));
        foreach (var row in details)
            results.Children.Add(Card(Paragraph($"{row.ModelId ?? "Model unavailable"} · {row.AgentId ?? "Agent unavailable"} · {row.State}\n" +
                 $"Provider-observed input/output {row.ProviderObservedInputTokens?.ToString("N0") ?? "unavailable"} / {row.ProviderObservedOutputTokens?.ToString("N0") ?? "unavailable"} · Progress, not confirmed usage\n" +
                $"Prepared input {Number(row.PreparedInputTokens)} · Context {Number(row.EffectiveContextTokens)} · Response budget {Number(row.ResponseBudgetTokens)}\n" +
                $"Cache tokens {Number(row.CachedTokens)} · Processed tokens {Number(row.ProcessedTokens)} · Reasoning reserve {Number(row.ReasoningReserveTokens)}\n" +
                $"Provider prompt/output ms {row.ProviderPromptMilliseconds?.ToString("N1") ?? "unavailable"} / {row.ProviderOutputMilliseconds?.ToString("N1") ?? "unavailable"}\n" +
                $"Generation host ms {row.GenerationHostMilliseconds?.ToString("N0") ?? "unavailable"} · Output scope {row.OutputTokenScope}\n" +
                $"{row.Origin} · {row.Basis} · {Local(row.ObservedAt)}")));
        results.Children.Add(Heading("Agent activity"));
        if (result.Activity.Length == 0) results.Children.Add(Paragraph("No recorded agent activity in this range."));
        foreach (var row in result.Activity.Take(8))
            results.Children.Add(Card(Paragraph(row.AgentId + "\n" + $"Generations {row.Generations}   Workflows {row.CompletedWorkflows} completed / {row.FailedWorkflows} failed   Tool receipts {row.ToolReceipts}")));
        results.Children.Add(Heading("Collection coverage"));
        results.Children.Add(Paragraph($"{coverage.StoredBytes / 1024d / 1024d:N2} MiB stored · " + (coverage.Earliest is null ? "No retained observations; coverage is unavailable." :
            coverage.Partial ? "Some observations may be missing. See collection details." : "No reported collection loss in retained observations.")));
        results.Children.Add(new Expander { Header = "Collection details", Content = Paragraph($"{coverage.RetainedEntities:N0} retained entities · {coverage.DuplicateReads:N0} repeated reads ignored\n" +
            $"Rejected {coverage.RejectedRecords:N0} · source drops {coverage.SourceDropped:N0} · returned rows {result.Rows.Length:N0}, omitted {result.OmittedRows:N0}\n" +
            "Query result: " + Local(result.GeneratedAt) + " · " + result.Schema) });
    }
    private static string? Id(TextBox box) => string.IsNullOrWhiteSpace(box.Text) ? null : box.Text.Trim();
    private static string Local(DateTimeOffset time) => time.ToLocalTime().ToString("MMM d, HH:mm:ss", CultureInfo.CurrentCulture);
    private static string Total(MetricTotal value) => value.Value is { } n ? n.ToString("N0", CultureInfo.CurrentCulture) + (value.Missing > 0 ? " · partial" : "") : "Unavailable";
    private static string Count(int n, int observations) => observations == 0 ? "Unavailable" : n.ToString("N0", CultureInfo.CurrentCulture);
    private static string Number(int? n) => n?.ToString(CultureInfo.CurrentCulture) ?? "unavailable";
    private static TextBox Filter(string id)
    { var box = new TextBox { Width = 148, Height = 30, Padding = new Thickness(5, 3, 5, 3) }; AutomationProperties.SetAutomationId(box, id); return box; }
    private static StackPanel Labeled(string title, FrameworkElement input)
    { var panel = new StackPanel { Margin = new Thickness(0, 0, 14, 8) }; panel.Children.Add(Text(title, 12)); panel.Children.Add(input); return panel; }
    private static TextBlock Text(string value, double size, FontWeight? weight = null) => new()
    { Text = value, FontSize = size, FontWeight = weight ?? FontWeights.Normal, TextWrapping = TextWrapping.Wrap };
    private static TextBlock Paragraph(string value)
    { var text = Text(value, 12); text.Margin = new Thickness(0, 4, 0, 8); return text; }
    private static TextBlock Heading(string value)
    { var text = Text(value, 17, FontWeights.SemiBold); text.Margin = new Thickness(0, 18, 0, 6); return text; }
    private static Border Card(UIElement child) => new()
    { Child = child, Padding = new Thickness(14), Margin = new Thickness(0, 0, 8, 8), CornerRadius = new CornerRadius(7),
        BorderThickness = new Thickness(1), BorderBrush = new SolidColorBrush(Color.FromArgb(65, 90, 120, 155)),
        Background = new SolidColorBrush(Color.FromArgb(12, 90, 120, 155)) };
    private static void AddCard(Panel panel, string title, string value, string detail)
    {
        var body = new StackPanel { Width = 204, MinHeight = 105 }; body.Children.Add(Text(title, 12));
        var number = Text(value, 23, FontWeights.SemiBold); number.Margin = new Thickness(0, 7, 0, 5); body.Children.Add(number); body.Children.Add(Text(detail, 11)); panel.Children.Add(Card(body));
    }
    public void Dispose() { if (disposed) return; disposed = true; Loaded -= OnLoaded; lifetime.Cancel(); lifetime.Dispose(); }
}

internal sealed class TokenTrend(MetricsTrend[] rows, bool providerObserved = false) : FrameworkElement
{
    private MetricTotal Value(MetricsTrend row) => providerObserved ? row.ProviderObservedOutputTokens : row.OutputTokens;
    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc); var ink = TryFindResource(DesktopThemeResourceKeys.TextBrush) as Brush ?? Brushes.SlateGray;
        var width = Math.Max(1, ActualWidth - 54); var height = Math.Max(1, ActualHeight - 48);
        var known = rows.Where(row => Value(row).Value is not null).ToArray();
        if (known.Length == 0) { Draw("No recorded token counts", 25, height / 2); return; }
        dc.DrawLine(new Pen(Brushes.LightSlateGray, 1), new Point(35, 10), new Point(35, height + 10));
        dc.DrawLine(new Pen(Brushes.LightSlateGray, 1), new Point(35, height + 10), new Point(width + 35, height + 10));
        var max = Math.Max(1, known.Select(row => Value(row).Value!.Value).DefaultIfEmpty(1).Max());
        Draw("0", 2, height + 1); Draw(max.ToString("N0", CultureInfo.CurrentCulture), 0, 0);
        var first = rows.Min(row => row.Day); var last = rows.Max(row => row.Day); var span = Math.Max(1, last.DayNumber - first.DayNumber);
        Point? previous = null; int? previousDay = null;
        foreach (var row in known)
        {
            var p = new Point(35 + width * (row.Day.DayNumber - first.DayNumber) / span,
                height + 10 - height * Value(row).Value!.Value / max);
            if (previous is { } before && previousDay == row.Day.DayNumber - 1) dc.DrawLine(new Pen(Brushes.DodgerBlue, 2), before, p);
            dc.DrawEllipse(Brushes.DodgerBlue, null, p, 3, 3); previous = p; previousDay = row.Day.DayNumber;
        }
        Draw(first.ToString("MMM d", CultureInfo.CurrentCulture), 35, height + 17);
        Draw(last.ToString("MMM d", CultureInfo.CurrentCulture), width - 10, height + 17);
        void Draw(string value, double x, double y) => dc.DrawText(new FormattedText(value, CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight, new Typeface("Segoe UI"), 11, ink, VisualTreeHelper.GetDpi(this).PixelsPerDip), new Point(x, y));
    }
}
