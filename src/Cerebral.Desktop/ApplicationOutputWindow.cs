using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace Cerebral.Desktop;

/// <summary>Owns the actual launched process and reports its lifecycle independently of a shell wrapper.</summary>
public sealed class ApplicationOutputWindow : Window
{
    private readonly Process process;
    private readonly TextBox output = Ui.Json();
    private readonly TextBlock state = Ui.Text("Starting", 13);
    private readonly Func<int, DateTimeOffset, string, int?, string, Task>? report;
    private bool exited;
    private bool stopRequested;
    private bool closed;
    public int ProcessId { get; }
    public DateTimeOffset ProcessStartedAt { get; }

    public ApplicationOutputWindow(string executable, Func<int, DateTimeOffset, string, int?, string, Task>? report = null)
    {
        this.report = report;
        Title = Path.GetFileNameWithoutExtension(executable) + " · Application output";
        Width = 900; Height = 580; MinWidth = 600; MinHeight = 360;
        WindowSizing.Attach(this);
        WindowStartupLocation = WindowStartupLocation.CenterScreen; Icon = Branding.IconFor("Orchestrator");
        output.IsReadOnly = true;
        System.Windows.Automation.AutomationProperties.SetAutomationId(output, "ApplicationOutput");
        System.Windows.Automation.AutomationProperties.SetAutomationId(state, "ApplicationExitStatus");
        Content = Ui.Page(Path.GetFileName(executable), executable, output,
            Ui.Button("Copy output", () => { try { Clipboard.SetText(output.Text); } catch (Exception ex) { state.Text = ex.Message; } }),
            Ui.Button("Stop application", StopApplication), state);
        process = Process.Start(new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetDirectoryName(executable)!,
            RedirectStandardOutput = true, RedirectStandardError = true
        }) ?? throw new IOException("Could not launch the application.");
        ProcessId = process.Id; ProcessStartedAt = process.StartTime.ToUniversalTime();
        state.Text = $"Running · process {ProcessId}";
        Closing += (_, e) =>
        {
            if (!exited && !process.HasExited && MessageBox.Show(this, "The application is still running. Stop it and close this output window?", "Close application", MessageBoxButton.YesNo) != MessageBoxResult.Yes)
            { e.Cancel = true; return; }
            StopApplication();
        };
        Closed += (_, _) => { closed = true; if (exited) process.Dispose(); };
        _ = ObserveAsync();
    }

    public void StopApplication()
    {
        stopRequested = true;
        try { if (!exited && !process.HasExited) process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
        catch (Exception ex) { state.Text = ex.Message; }
    }
    private async Task ReportAsync(string status, int? exit)
    {
        if (report is null) return;
        var bounded = output.Text.Length > 8192 ? output.Text[^8192..] : output.Text;
        try { await report(ProcessId, ProcessStartedAt, status, exit, bounded.Replace("\0", "")); }
        catch (Exception ex) { state.Text += " · Service feedback unavailable: " + ex.Message; }
    }
    private async Task ObserveAsync()
    {
        try
        {
            // Start draining immediately so verbose startup cannot block a child while feedback is sent.
            using var drainStop = new CancellationTokenSource();
            var drain = Task.WhenAll(ReadAsync(process.StandardOutput, drainStop.Token), ReadAsync(process.StandardError, drainStop.Token));
            await ReportAsync("running", null);
            var readiness = ReportReadinessAsync();
            await process.WaitForExitAsync();
            await readiness;
            try { await drain.WaitAsync(TimeSpan.FromSeconds(2)); }
            catch (TimeoutException)
            {
                output.AppendText("\n[Output drain stopped after process exit; a descendant kept a stream open.]\n");
                drainStop.Cancel();
                // A descendant may retain inherited pipes. Do not delay the observed exit while they drain.
                _ = drain.ContinueWith(t => { _ = t.Exception; }, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                output.AppendText("\n[Some process output could not be read: " + ex.Message + "]\n");
            }
            var code = process.ExitCode; exited = true;
            state.Text = $"Exited with code {code}";
            await ReportAsync(stopRequested ? "stopped" : code == 0 ? "exited" : "failed", code);
        }
        catch (Exception ex) { exited = true; state.Text = "Application output: " + ex.Message; }
        finally { if (closed) process.Dispose(); }
    }
    private async Task ReportReadinessAsync()
    {
        await Task.Delay(750);
        try
        {
            if (process.HasExited || stopRequested) return;
            process.Refresh();
            if (process.MainWindowHandle != IntPtr.Zero && process.Responding)
                await ReportAsync("ready", null);
        }
        catch (InvalidOperationException) { }
    }
    private async Task ReadAsync(StreamReader reader, CancellationToken token)
    {
        var buffer = new char[2048]; int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory(), token)) > 0)
        {
            output.AppendText(new string(buffer, 0, count));
            if (output.Text.Length > 128_000) output.Text = "[earlier output omitted]\n" + output.Text[^100_000..];
            output.ScrollToEnd();
        }
    }
}
