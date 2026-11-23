using System.Runtime.InteropServices;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;

namespace Cerebral.WindowWatchPlugin;

internal sealed partial class WatchWindow : Window
{
    private readonly ComboBox targets = new() { DisplayMemberPath = nameof(WindowTarget.Label) };
    private readonly TextBox endpoint = new() { MaxLength = 512 };
    private readonly TextBox model = new() { MaxLength = 128 };
    private readonly TextBox seconds = new() { Text = "30", MaxLength = 4, Width = 58 };
    private readonly TextBox prompt = new()
    {
        AcceptsReturn = true, MaxLength = 2400, Height = 60,
        TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto
    };
    private readonly CheckBox keepReplies = new() { Content = "Remember recent replies this session" };
    private readonly ComboBox replyLimit = new() { ItemsSource = Enumerable.Range(1, 10).ToArray(), SelectedItem = 3, Width = 55 };
    private readonly CheckBox onlyChanges = new() { Content = "Skip unchanged snapshots", IsChecked = true };
    private readonly CheckBox textDetail = new() { Content = "Text detail (larger, lossless image)" };
    private readonly CheckBox showOutline = new() { Content = "Outline selected window", IsChecked = true };
    private readonly CheckBox stayOnTop = new() { Content = "Keep panel on top", IsChecked = true };
    private readonly Button refresh = new() { Content = "Refresh windows" };
    private readonly Button analyze = new();
    private readonly Button start = new();
    private readonly Button stop = new();
    private readonly Button clear = new() { Content = "Clear replies" };
    private readonly TextBox output = new()
    {
        IsReadOnly = true, TextWrapping = TextWrapping.Wrap, AcceptsReturn = true,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(8)
    };
    private readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };
    private readonly TextBlock countdown = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly VisionClient vision = new();
    private readonly ReplyMemory memory = new();
    private WindowOutline? outline;
    private WindowSelection? selection;
    private CancellationTokenSource? active;
    private bool auto;
    private bool paused;
    private bool closed;
    private bool modelRequestInFlight;
    private int interval;
    private long nextCapture;
    private long contextRevision;
    private string? lastPixelHash;

    internal WatchWindow()
    {
        Title = "Window Watch";
        Width = 438;
        Height = 345;
        MinWidth = 360;
        MinHeight = ReadingMinHeight;
        Topmost = true;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ApplyPanelTheme();
        Content = BuildLayout();

        Loaded += (_, _) => { RefreshTargets(); timer.Start(); };
        Closed += (_, _) =>
        {
            closed = true;
            timer.Stop();
            active?.Cancel();
            selection?.Dispose();
            outline?.Close();
            vision.Dispose();
            memory.Clear();
        };
        refresh.Click += (_, _) => RefreshTargets();
        targets.SelectionChanged += (_, _) => BindSelection();
        analyze.Click += async (_, _) => await AnalyzeAsync(manual: true);
        start.Click += async (_, _) =>
        {
            if (!TrySettings(out _, out _) || !TryInterval()) return;
            if (!paused) lastPixelHash = null;
            paused = false;
            auto = true;
            SetOptionsVisible(false);
            UpdateControls();
            await AnalyzeAsync(manual: false);
        };
        stop.Click += (_, _) =>
        {
            var wasAuto = auto;
            var wasAnalyzing = modelRequestInFlight;
            paused = wasAuto;
            Halt(wasAnalyzing
                ? "Paused locally. Check model activity before continuing."
                : wasAuto ? "Automatic capture paused." : "Snapshot canceled.");
        };
        clear.Click += (_, _) =>
        {
            ResetReplies();
            status.Text = "Replies cleared. The next analysis will use your base prompt.";
        };
        keepReplies.Checked += (_, _) => UpdateControls();
        keepReplies.Unchecked += (_, _) => { ResetReplies(); UpdateControls(); };
        replyLimit.SelectionChanged += (_, _) => memory.Trim(ReplyLimit);
        prompt.TextChanged += (_, _) => InvalidateAnalysis();
        textDetail.Checked += (_, _) => InvalidateAnalysis();
        textDetail.Unchecked += (_, _) => InvalidateAnalysis();
        stayOnTop.Checked += (_, _) => Topmost = true;
        stayOnTop.Unchecked += (_, _) => Topmost = false;
        timer.Tick += async (_, _) => await TickAsync();
        status.Text = "Open Options to choose a window and vision model.";
        UpdateControls();
    }

    private int ReplyLimit => replyLimit.SelectedItem is int value ? value : 3;

    private void RefreshTargets()
    {
        var previous = selection?.Target;
        var available = WindowTarget.List(new WindowInteropHelper(this).Handle);
        targets.ItemsSource = available;
        if (previous is not null)
            targets.SelectedItem = available.FirstOrDefault(x =>
                x.Handle == previous.Handle && x.ProcessId == previous.ProcessId &&
                x.ProcessStartUtc == previous.ProcessStartUtc);
    }

    private void BindSelection()
    {
        selection?.Dispose();
        selection = null;
        paused = false;
        ResetReplies();
        if (targets.SelectedItem is WindowTarget target && target.StillSelected())
        {
            try
            {
                selection = new WindowSelection(target);
                status.Text = $"Selected: {target.Label}";
            }
            catch (InvalidOperationException error)
            {
                status.Text = error.Message;
            }
        }
        else
            status.Text = "Select a window to analyze.";
        UpdateControls();
    }

    private bool TrySettings(out Uri url, out string modelName)
    {
        modelName = model.Text.Trim();
        if (!VisionClient.TryEndpoint(endpoint.Text, out url))
        {
            SetOptionsVisible(true);
            status.Text = "Enter a local HTTP image endpoint ending in /v1/chat/completions.";
            return false;
        }
        if (modelName.Length == 0)
        {
            SetOptionsVisible(true);
            status.Text = "Enter the name of the vision model served by that endpoint.";
            return false;
        }
        return true;
    }

    private bool TryInterval()
    {
        if (!int.TryParse(seconds.Text, NumberStyles.None, CultureInfo.InvariantCulture, out interval) ||
            interval is < 5 or > 3600)
        {
            SetOptionsVisible(true);
            status.Text = "Enter an interval from 5 to 3600 seconds.";
            return false;
        }
        return true;
    }

    private async Task TickAsync()
    {
        if (closed) return;
        if (selection is { Destroyed: true })
        {
            Halt("The selected window closed. Refresh the list and select a window.");
            targets.SelectedItem = null;
            status.Text = "The selected window closed. Refresh the list and select a window.";
        }
        if (showOutline.IsChecked == true && (auto || active is { IsCancellationRequested: false }) && selection is { } selected)
        {
            outline ??= new WindowOutline();
            outline.Track(selected.Target);
        }
        else
            outline?.Track(null);

        if (!auto || active is not null)
        {
            countdown.Text = active is null ? (paused ? "Paused" : "") : "Working…";
            return;
        }
        var remaining = (nextCapture - Stopwatch.GetTimestamp()) / (double)Stopwatch.Frequency;
        countdown.Text = remaining > 0 ? $"Next in {Math.Ceiling(remaining):0}s" : "Capturing…";
        if (remaining <= 0) await AnalyzeAsync(manual: false);
    }

    private async Task AnalyzeAsync(bool manual)
    {
        if (closed || active is not null || selection is not { } selected) return;
        if (!TrySettings(out var url, out var modelName))
        {
            auto = false;
            UpdateControls();
            return;
        }

        if (manual) SetOptionsVisible(false);
        using var request = new CancellationTokenSource();
        active = request;
        var token = request.Token;
        var revision = contextRevision;
        var focus = prompt.Text.Trim();
        var highDetail = textDetail.IsChecked == true;
        var skipUnchanged = !manual && onlyChanges.IsChecked == true;
        UpdateControls();
        try
        {
            if (selected.Destroyed || !selected.Target.StillSelected())
                throw new InvalidOperationException("The selected window is gone. Refresh the list and select it again.");
            if (!selected.Target.CanSample())
            {
                status.Text = auto
                    ? "Waiting for the selected window to be restored."
                    : "Restore the selected window and click Analyze now.";
                return;
            }

            status.Text = $"Capturing {selected.Target.Title}…";
            var snapshot = await PaoWindowCapture.CaptureAsync(selected.Target, highDetail);
            token.ThrowIfCancellationRequested();
            if (selected.Destroyed || !selected.Target.StillSelected())
                throw new InvalidOperationException("The selected window changed during capture. Select it again.");
            if (revision != contextRevision) return;
            if (skipUnchanged && snapshot.PixelHash == lastPixelHash)
            {
                status.Text = "Window unchanged. Waiting for the next check.";
                return;
            }

            status.Text = "Analyzing the snapshot…";
            var image = await Task.Run(() => snapshot.Encode(highDetail), token);
            try
            {
                token.ThrowIfCancellationRequested();
                if (revision != contextRevision) return;
                var notes = keepReplies.IsChecked == true ? memory.Snapshot() : "";
                modelRequestInFlight = true;
                var answer = await vision.AnalyzeAsync(url, modelName, focus, notes, image, highDetail, token);
                if (closed || token.IsCancellationRequested || selected.Destroyed || revision != contextRevision) return;
                output.Text = answer;
                output.ScrollToHome();
                if (keepReplies.IsChecked == true) memory.Add(answer, ReplyLimit);
                lastPixelHash = snapshot.PixelHash;
                status.Text = $"Analyzed {selected.Target.Title} at {DateTime.Now:t}.";
            }
            finally
            {
                CryptographicOperations.ZeroMemory(image);
                modelRequestInFlight = false;
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error) when (error is IOException or InvalidDataException or InvalidOperationException or
            ExternalException or ArgumentException or UnauthorizedAccessException or NotSupportedException)
        {
            auto = false;
            if (!closed && !token.IsCancellationRequested)
                status.Text = error is InvalidOperationException or InvalidDataException
                    ? error.Message
                    : "Window Watch could not complete the analysis. Check the selected window and local model.";
        }
        finally
        {
            active = null;
            if (!closed)
            {
                nextCapture = Stopwatch.GetTimestamp() + interval * Stopwatch.Frequency;
                UpdateControls();
            }
        }
    }

    private void Halt(string message)
    {
        auto = false;
        active?.Cancel();
        outline?.Track(null);
        status.Text = message;
        UpdateControls();
    }

    private void ResetReplies()
    {
        memory.Clear();
        output.Clear();
        InvalidateAnalysis();
    }

    private void InvalidateAnalysis()
    {
        contextRevision++;
        lastPixelHash = null;
    }

    private void UpdateControls()
    {
        var busy = active is not null;
        var available = selection is { Destroyed: false };
        refresh.IsEnabled = targets.IsEnabled = endpoint.IsEnabled = model.IsEnabled = seconds.IsEnabled = !auto && !busy;
        analyze.IsEnabled = available && !busy;
        start.IsEnabled = available && !busy && !auto;
        stop.IsEnabled = auto || active is { IsCancellationRequested: false };
        start.Tag = auto ? "active" : null;
        SetActionLabel(start, auto ? "Automatic capture active" : paused ? "Resume auto" : "Start auto");
        start.ToolTip = $"{start.ToolTip}. Pause other AI work first.";
        SetActionLabel(stop, auto ? "Pause auto" : "Cancel snapshot");
        replyLimit.IsEnabled = keepReplies.IsChecked == true;
        modeCaption.Text = auto ? $"Auto · {interval}s" : paused ? "Auto paused" : "Window Watch";
        modeCaption.Text += keepReplies.IsChecked == true ? " · recent replies on" : " · fresh input";
        var title = selection?.Target.Title;
        targetCaption.Text = title ?? "Choose a window";
        targetCaption.ToolTip = title;
        if (!auto && !busy) countdown.Text = paused ? "Paused" : "";
    }
}
