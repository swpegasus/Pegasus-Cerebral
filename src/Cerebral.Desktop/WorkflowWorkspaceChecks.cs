using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Cerebral.Client;
using Cerebral.Contracts.Workflows;
using Cerebral.Core;

namespace Cerebral.Desktop;

/// <summary>
/// Opt-in shown-window acceptance against an already prepared, isolated Worker pipe.
/// Answers only the explicitly supplied example.review run. It does not start or stop the Worker.
/// </summary>
public static class WorkflowWorkspaceChecks
{
    public static int CheckService(Application app, LocalWorkerEndpoint endpoint, string runId, string? previewPath = null)
    {
        ArgumentNullException.ThrowIfNull(app); ArgumentNullException.ThrowIfNull(endpoint);
        endpoint.Validate();
        if (!app.Dispatcher.CheckAccess() || app.Windows.Count != 0)
            throw new InvalidOperationException("Run this shown-workflow check on a dedicated WPF application thread before opening other windows.");
        if (string.IsNullOrWhiteSpace(runId)) throw new ArgumentException("An isolated review workflow run ID is required.", nameof(runId));
        if (previewPath is not null && (!Path.IsPathFullyQualified(previewPath) || !previewPath.EndsWith(".png", StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("The optional shown-window preview requires an explicit absolute PNG path.", nameof(previewPath));

        app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        using var client = new CerebralClient(endpoint);
        using var observer = new CerebralClient(endpoint);
        var watch = Stopwatch.StartNew(); var checks = 0; var answerSubmissions = 0; var commands = 0;
        var started = false; var finished = false; var shown = false; var reportedErrors = 0; int? confirmedWordCount = null;
        double actualDpiX = 0, actualDpiY = 0, clientWidth = 0, clientHeight = 0;
        async Task<CommandResult> Dispatch(CommandRequest request, CancellationToken token)
        {
            using var bounded = CancellationTokenSource.CreateLinkedTokenSource(token, deadline.Token);
            Interlocked.Increment(ref commands);
            if (request.Operation == "jobs.workflow.answer") Interlocked.Increment(ref answerSubmissions);
            return await client.CommandAsync(request.Operation, request.Arguments, request.ProfileId, bounded.Token);
        }
        using var workspace = new WorkflowWorkspace(Dispatch, () => Array.Empty<AgentProfile>(), (_, error) =>
        { if (error) Interlocked.Increment(ref reportedErrors); });
        var window = new Window
        {
            Title = "Pegasus Cerebral · workflow acceptance", Width = 1024, Height = 768,
            MinWidth = 720, MinHeight = 480, WindowStartupLocation = WindowStartupLocation.CenterScreen,
            Background = Brushes.White, Content = workspace, UseLayoutRounding = true
        };
        var watchdog = new DispatcherTimer { Interval = TimeSpan.FromSeconds(65) };
        void Finish(int exit)
        {
            if (finished) return; finished = true;
            watchdog.Stop(); deadline.Cancel(); workspace.Dispose(); window.Close(); app.Shutdown(exit);
        }
        watchdog.Tick += (_, _) =>
        {
            Console.Error.WriteLine(JsonSerializer.Serialize(new { success = false, checks, runId, windowShown = shown,
                error = "The shown workflow acceptance exceeded its outer deadline.", answerSubmissions, commands }));
            Finish(1);
        };
        watchdog.Start();
        void Check(bool condition, string label)
        { if (!condition) throw new InvalidOperationException(label); checks++; }
        async Task<WorkflowRun> SavedRun() => await observer.GetAsync<WorkflowRun>("jobs.workflow.get", new { id = runId }, cancellationToken: deadline.Token);
        async Task Refresh()
        {
            await workspace.RefreshAsync().WaitAsync(deadline.Token);
            await window.Dispatcher.InvokeAsync(() => window.UpdateLayout(), DispatcherPriority.Render, deadline.Token);
        }
        async Task WaitFor(Func<Task<bool>> predicate, TimeSpan maximum)
        {
            var until = watch.Elapsed + maximum;
            while (!await predicate())
            {
                deadline.Token.ThrowIfCancellationRequested();
                if (watch.Elapsed >= until) throw new TimeoutException("The expected review workflow state did not arrive within the acceptance deadline.");
                await Task.Delay(200, deadline.Token);
            }
        }

        window.ContentRendered += async (_, _) =>
        {
            if (started) return; started = true;
            var exit = 0;
            try
            {
                await WorkflowWorkspaceConnectionChecks.VerifyAsync().WaitAsync(deadline.Token);
                shown = window.IsVisible && new WindowInteropHelper(window).Handle != nint.Zero;
                Check(shown && PresentationSource.FromVisual(window) is HwndSource, "Acceptance requires a real shown WPF HWND.");
                Check(client.IsLocalPipe && observer.IsLocalPipe, "Acceptance must use the explicitly bound Worker pipe without HTTP fallback.");
                var dpi = VisualTreeHelper.GetDpi(workspace); actualDpiX = dpi.PixelsPerInchX; actualDpiY = dpi.PixelsPerInchY;
                var initial = await SavedRun();
                Check(initial.Id == runId && initial.Submission.DefinitionId == "example.review:review" && !initial.IsTerminal,
                    "Only the explicitly prepared, nonterminal example review workflow may be answered.");
                var hidden = initial.Submission.AssignmentText;
                Check(!string.IsNullOrWhiteSpace(hidden) && hidden != initial.Submission.UserText,
                    "Seed a distinct hidden assignment in the isolated run to exercise display/context separation.");
                await workspace.OpenRunAsync(runId).WaitAsync(deadline.Token);
                await WaitFor(async () =>
                {
                    await Refresh();
                    return Find<TextBox>(workspace, "WorkflowAnswer-label") is { IsVisible: true } &&
                        Find<ComboBox>(workspace, "WorkflowAnswer-style") is { IsVisible: true };
                }, TimeSpan.FromSeconds(15));
                var waiting = await SavedRun();
                Check(waiting.Status == WorkflowStatus.Waiting && waiting.Questions?.Fields.Select(x => x.Id).ToHashSet(StringComparer.Ordinal).SetEquals(["label", "style"]) == true,
                    "The shown fields must correspond to the actual service's two-question set.");
                var form = Find<Border>(workspace, "WorkflowQuestions") ?? throw new InvalidOperationException("Question form is missing.");
                var transcript = Find<ScrollViewer>(workspace, "WorkflowTranscript") ?? throw new InvalidOperationException("Workflow transcript is missing.");
                var label = Find<TextBox>(workspace, "WorkflowAnswer-label")!;
                var style = Find<ComboBox>(workspace, "WorkflowAnswer-style")!;
                var submit = Find<Button>(workspace, "WorkflowSubmitAnswers") ?? throw new InvalidOperationException("Native answer submit button is missing.");
                window.UpdateLayout(); clientWidth = workspace.ActualWidth; clientHeight = workspace.ActualHeight;
                Check(Math.Abs(window.ActualWidth - 1024) <= 1 && Math.Abs(window.ActualHeight - 768) <= 1 && clientWidth > 900 && clientHeight > 600,
                    "The actual 1024×768 window must provide a bounded native client surface.");
                Check(Inside(form, workspace) && Inside(transcript, workspace) && transcript.ActualHeight >= 120 &&
                    Bounds(transcript, workspace).Bottom <= Bounds(form, workspace).Top + 1,
                    "The shown question form and transcript must fit without overlapping.");
                label.BringIntoView(); label.Focus(); window.UpdateLayout();
                Check(label.IsEnabled && Inside(label, form), "The label input must remain visible and usable in the form viewport.");
                label.Text = "Passage";
                style.BringIntoView(); window.UpdateLayout();
                Check(style.Items.Cast<object>().Any(x => Equals(x, "concise")) && style.IsEnabled && Inside(style, form),
                    "The native choice field must expose the concise option inside the form.");
                style.SelectedItem = "concise";
                Check(!RenderedText(workspace).Contains(hidden!, StringComparison.Ordinal), "Private assignment context must not appear in the shown workflow UI.");
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle, deadline.Token);
                Check(submit.IsVisible && submit.IsEnabled && Inside(submit, form), "The native submit action must be visible, enabled and inside its form.");
                // Exercise the same routed click handler as the real button. Do not call AnswerAsync or the answer command directly.
                submit.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, submit));
                WorkflowRun? completed = null;
                await WaitFor(async () =>
                {
                    completed = await SavedRun();
                    if (completed.Status is WorkflowStatus.Failed or WorkflowStatus.Cancelled)
                        throw new InvalidOperationException("The real review workflow terminated without completing the requested measurement.");
                    return completed.Status == WorkflowStatus.Completed;
                }, TimeSpan.FromSeconds(30));
                Check(answerSubmissions == 1, "One routed submission must issue one actual service answer request.");
                Check(completed?.Result is { ValueKind: JsonValueKind.Object } result &&
                    result.GetProperty("wordCount").GetInt32() == 3 && result.GetProperty("label").GetString() == "Passage" &&
                    result.GetProperty("style").GetString() == "concise", "The service must durably complete with three words and both supplied answers.");
                confirmedWordCount = 3;
                await Refresh();
                Check(form.Visibility == Visibility.Collapsed && Find<TextBox>(workspace, "WorkflowAnswer-label") is null && Find<ComboBox>(workspace, "WorkflowAnswer-style") is null,
                    "After acknowledgement the editable question controls must leave the UI.");
                var visible = RenderedText(workspace);
                Check(visible.Contains("Passage", StringComparison.Ordinal) && visible.Contains("concise", StringComparison.Ordinal) && visible.Contains("3 words", StringComparison.Ordinal),
                    "The transcript must show the accepted answers and actual completed measurement.");
                Check(!visible.Contains(hidden!, StringComparison.Ordinal), "Completing a workflow must not expose the hidden assignment in its transcript.");
                var activityBranches = Descendants(workspace).OfType<Expander>()
                    .Where(x => x.Header is string header && header.StartsWith("Activity", StringComparison.Ordinal)).ToArray();
                Check(activityBranches.Length == 1 && activityBranches.All(x => !((string)x.Header).Contains("Service updates", StringComparison.Ordinal)),
                    "One workflow turn must keep service updates as rows in its activity branch instead of creating a separate service-updates branch.");
                // Hydrate activity on demand too, so a collapsed row cannot conceal a display leak or broken argument renderer.
                foreach (var row in Descendants(workspace).OfType<Expander>().ToArray()) row.IsExpanded = true;
                window.UpdateLayout();
                foreach (var row in Descendants(workspace).OfType<Expander>().ToArray()) row.IsExpanded = true;
                window.UpdateLayout();
                Check(!RenderedText(workspace).Contains(hidden!, StringComparison.Ordinal), "Expanded native activity must retain hidden-context separation.");
                Check(reportedErrors == 0, "The real workflow UI must not report a service or rendering error during acceptance.");
                Check(Inside(transcript, workspace) && transcript.ActualHeight >= 200, "Completion must return the question space to the transcript.");
                if (previewPath is not null)
                {
                    foreach (var row in Descendants(workspace).OfType<Expander>().ToArray()) row.IsExpanded = false;
                    transcript.ScrollToEnd(); window.UpdateLayout();
                    await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render, deadline.Token);
                    SaveShownContent(workspace, previewPath, dpi);
                }
                Console.WriteLine(JsonSerializer.Serialize(new
                {
                    success = true, checks, runId, windowShown = shown, actualHwnd = true,
                    transport = "authenticated-local-pipe", serviceCommandsExecuted = true, modelInvoked = false,
                    answerSubmissions, commands, wordCount = confirmedWordCount,
                    requestedWindowDips = "1024x768", clientWidth, clientHeight, actualDpiX, actualDpiY,
                    capture = previewPath is null ? null : "WPF render of shown window content at actual monitor DPI; not an OS screenshot",
                    previewPath, elapsedMilliseconds = watch.ElapsedMilliseconds
                }));
            }
            catch (Exception error)
            {
                exit = 1;
                Console.Error.WriteLine(JsonSerializer.Serialize(new
                {
                    success = false, checks, runId, windowShown = shown, transport = "authenticated-local-pipe",
                    answerSubmissions, commands, confirmedWordCount, reportedErrors,
                    error = error.Message, errorType = error.GetType().Name, elapsedMilliseconds = watch.ElapsedMilliseconds
                }));
            }
            finally
            {
                Finish(exit);
            }
        };
        // The test owns this one window and application lifetime. It never closes another program.
        return app.Run(window);
    }

    private static T? Find<T>(DependencyObject root, string id) where T : FrameworkElement =>
        Descendants(root).OfType<T>().FirstOrDefault(x => AutomationProperties.GetAutomationId(x) == id);

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        var pending = new Stack<DependencyObject>(); pending.Push(root); var remaining = 20_000;
        while (pending.TryPop(out var next))
        {
            if (--remaining < 0) throw new InvalidOperationException("The shown acceptance visual tree exceeds its bounded traversal size.");
            yield return next;
            for (var i = VisualTreeHelper.GetChildrenCount(next) - 1; i >= 0; i--) pending.Push(VisualTreeHelper.GetChild(next, i));
        }
    }

    private static Rect Bounds(FrameworkElement child, FrameworkElement ancestor) =>
        child.TransformToAncestor(ancestor).TransformBounds(new Rect(new Point(), child.RenderSize));

    private static bool Inside(FrameworkElement child, FrameworkElement ancestor)
    {
        var bounds = Bounds(child, ancestor);
        return child.ActualWidth > 0 && child.ActualHeight > 0 &&
            double.IsFinite(bounds.X) && double.IsFinite(bounds.Y) && bounds.Left >= -1 && bounds.Top >= -1 &&
            bounds.Right <= ancestor.ActualWidth + 1 && bounds.Bottom <= ancestor.ActualHeight + 1;
    }

    private static string RenderedText(DependencyObject root)
    {
        var text = new StringBuilder();
        foreach (var item in Descendants(root))
        {
            switch (item)
            {
                case RichTextBox rich: text.AppendLine(new TextRange(rich.Document.ContentStart, rich.Document.ContentEnd).Text); break;
                case TextBox box: text.AppendLine(box.Text); break;
                case TextBlock label: text.AppendLine(label.Text); break;
                case ContentControl { Content: string content }: text.AppendLine(content); break;
            }
            if (text.Length > 2 * 1024 * 1024) throw new InvalidOperationException("The shown acceptance text exceeds its bounded inspection size.");
        }
        return text.ToString();
    }

    private static void SaveShownContent(FrameworkElement content, string path, DpiScale dpi)
    {
        if (PresentationSource.FromVisual(content) is not HwndSource) throw new InvalidOperationException("Only shown native content can be captured.");
        var width = checked((int)Math.Ceiling(content.ActualWidth * dpi.DpiScaleX));
        var height = checked((int)Math.Ceiling(content.ActualHeight * dpi.DpiScaleY));
        if (width is < 1 or > 8192 || height is < 1 or > 8192) throw new InvalidOperationException("The native capture exceeds its supported dimensions.");
        var bitmap = new RenderTargetBitmap(width, height, dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        bitmap.Render(content);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = new FileStream(Path.GetFullPath(path), FileMode.Create, FileAccess.Write, FileShare.None);
        encoder.Save(file);
    }
}
