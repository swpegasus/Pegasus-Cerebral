using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Cerebral.Client;
using Cerebral.Core;
using Cerebral.Desktop;
using Cerebral.Desktop.Plugins;

namespace Cerebral.Orchestrator;

public sealed partial class OrchestratorWindow
{
    private readonly TextBlock systemStatusText = Ui.Text("Cerebral metrics are starting…", 11, Ui.Muted);
    private readonly TextBlock systemWorkStateText = Ui.Text("Hold and model status are updating…", 11, Ui.Ink);
    private readonly TextBlock systemTokenStatusText = Ui.Text("Tokens — in / — out", 11, Ui.Muted);
    private readonly TextBlock systemSlotStatusText = Ui.Text("Local: — free / — total / — quarantined", 11, Ui.Muted);
    private readonly TextBlock remoteSlotStatusText = Ui.Text("Remote: — free · workers unknown", 11, Ui.Muted);
    private readonly WrapPanel desktopStatusItems = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly DispatcherTimer systemStatusTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private Border? systemStatusBar;
    private Popup? systemStatusDetailsPopup;
    private Button? systemStatusDetailsButton;
    private StatusMetricLine? systemMetricLine;
    private StatusMetricLine? tokenMetricLine;
    private bool systemStatusRefreshActive;
    private bool systemStatusInitialized;

    private FrameworkElement BuildSystemStatusBar()
    {
        if (!systemStatusInitialized)
        {
            systemStatusInitialized = true;
            systemStatusTimer.Tick += async (_, _) => await RefreshSystemStatusAsync();
            Loaded += async (_, _) =>
            {
                ApplySystemStatusVisibility();
                systemStatusTimer.Start();
                await RefreshSystemStatusAsync();
            };
            Closed += (_, _) => { systemStatusTimer.Stop(); CloseSystemStatusDetails(); };
            Deactivated += (_, _) => CloseSystemStatusDetails();
            PreviewKeyDown += (_, args) =>
            {
                if (args.Key != Key.Escape || systemStatusDetailsPopup?.IsOpen != true) return;
                CloseSystemStatusDetails(); args.Handled = true;
            };
        }

        systemStatusText.TextTrimming = TextTrimming.CharacterEllipsis;
        systemStatusText.TextWrapping = TextWrapping.NoWrap;
        systemStatusText.VerticalAlignment = VerticalAlignment.Center;
        AutomationProperties.SetAutomationId(systemStatusText, "SystemStatusMetrics");
        AutomationProperties.SetName(systemStatusText, "Local Cerebral and Worker resource usage, managed slots and queued work");
        systemTokenStatusText.TextWrapping = TextWrapping.NoWrap;
        systemTokenStatusText.VerticalAlignment = VerticalAlignment.Center;
        systemTokenStatusText.Margin = new Thickness(12, 0, 0, 0);
        AutomationProperties.SetAutomationId(systemTokenStatusText, "SystemStatusTokens");
        AutomationProperties.SetName(systemTokenStatusText, "Total active model throughput; confirmed token totals in details");
        systemMetricLine ??= new(systemStatusText,
        [
            (StatusMetricIcon.Cpu, "CPU"), (StatusMetricIcon.Ram, "RAM"),
            (null, "GPU"), (null, "VRAM"), (StatusMetricIcon.Model, "Slots"),
            (StatusMetricIcon.Queue, "Queued")
        ]);
        tokenMetricLine ??= new(systemTokenStatusText, [(StatusMetricIcon.Model, "Tokens")]);
        var content = new Grid();
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var primary = BuildPrimaryStatusControls();
        // Slot availability and the independently observed occupants are the primary status.
        // Keep resource telemetry available without turning allocator subtraction into occupancy.
        systemTokenStatusText.Margin = new Thickness(0, 6, 0, 0);
        var resourcesPanel = new StackPanel { MaxWidth = 760, Margin = new Thickness(6) };
        var resourceSummary = Ui.Text("", 11, Ui.Muted);
        resourceSummary.TextWrapping = TextWrapping.Wrap;
        resourceSummary.SetBinding(TextBlock.TextProperty, new Binding(nameof(FrameworkElement.ToolTip))
            { Source = systemStatusText, Mode = BindingMode.OneWay });
        resourcesPanel.Children.Add(resourceSummary);
        resourcesPanel.Children.Add(systemTokenStatusText);
        var resourceDetails = new Expander
        {
            Header = "Resources", Content = resourcesPanel, IsExpanded = true,
            Margin = new Thickness(0, 8, 0, 0), VerticalAlignment = VerticalAlignment.Top
        };
        AutomationProperties.SetAutomationId(resourceDetails, "SystemResourceDetails");
        AutomationProperties.SetName(resourceDetails, "CPU, memory, GPU, VRAM, tokens and detailed counters");
        // Keep accepted-work counts in expandable details, not beside allocator capacity.
        // The existing independently guarded ownership reader continues updating this source.
        var slotDetailText = new TextBox
        {
            IsReadOnly = true, TextWrapping = TextWrapping.Wrap, AcceptsReturn = true,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            MaxWidth = 760, MaxHeight = 300,
            Background = Ui.Paper, Foreground = Ui.Muted, BorderThickness = new Thickness(0),
            FontSize = 11, Padding = new Thickness(6)
        };
        slotDetailText.SetBinding(TextBox.TextProperty, new Binding(nameof(FrameworkElement.ToolTip))
        {
            Source = inferenceOwnershipStatusText, Mode = BindingMode.OneWay,
            TargetNullValue = "Slot ownership details have not been observed on this connection."
        });
        AutomationProperties.SetName(slotDetailText, "Observed slot occupants, accepted work and source details");
        var slotDetails = new Expander
        {
            Header = "Slot details", Content = slotDetailText, IsExpanded = true,
            Margin = new Thickness(0, 8, 0, 0), VerticalAlignment = VerticalAlignment.Top
        };
        AutomationProperties.SetAutomationId(slotDetails, "SystemInferenceOwnership");
        AutomationProperties.SetName(slotDetails, "Slot details");
        slotDetails.SetBinding(AutomationProperties.HelpTextProperty,
            new Binding(nameof(FrameworkElement.ToolTip)) { Source = inferenceOwnershipStatusText, Mode = BindingMode.OneWay });
        slotDetails.SetBinding(FrameworkElement.ToolTipProperty,
            new Binding(nameof(FrameworkElement.ToolTip)) { Source = inferenceOwnershipStatusText, Mode = BindingMode.OneWay });
        systemStatusText.SetBinding(AutomationProperties.HelpTextProperty,
            new Binding(nameof(FrameworkElement.ToolTip)) { Source = systemStatusText, Mode = BindingMode.OneWay });
        var remoteDetailText = new TextBox
        {
            IsReadOnly = true, TextWrapping = TextWrapping.Wrap, AcceptsReturn = true,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            MaxWidth = 760, MaxHeight = 300,
            Background = Ui.Paper, Foreground = Ui.Muted, BorderThickness = new Thickness(0),
            FontSize = 11, Padding = new Thickness(6)
        };
        remoteDetailText.SetBinding(TextBox.TextProperty, new Binding(nameof(FrameworkElement.ToolTip))
        {
            Source = remoteSlotStatusText, Mode = BindingMode.OneWay,
            TargetNullValue = "Remote worker observations are unavailable."
        });
        resourcesPanel.Children.Add(remoteDetailText);
        systemSlotStatusText.TextTrimming = TextTrimming.CharacterEllipsis;
        systemSlotStatusText.TextWrapping = TextWrapping.NoWrap;
        AutomationProperties.SetAutomationId(systemSlotStatusText, "SystemSlotAvailability");
        AutomationProperties.SetName(systemSlotStatusText, "Local managed inference slots, remote worker observations and queued work");
        remoteSlotStatusText.TextTrimming = TextTrimming.CharacterEllipsis;
        remoteSlotStatusText.TextWrapping = TextWrapping.NoWrap;
        AutomationProperties.SetAutomationId(remoteSlotStatusText, "SystemRemoteSlotAvailability");
        AutomationProperties.SetName(remoteSlotStatusText, "Remote workers and separately observed inference slots");
        inferenceOwnershipStatusText.TextTrimming = TextTrimming.CharacterEllipsis;
        inferenceOwnershipStatusText.TextWrapping = TextWrapping.NoWrap;
        AutomationProperties.SetAutomationId(inferenceOwnershipStatusText, "SystemSlotOccupants");
        AutomationProperties.SetName(inferenceOwnershipStatusText, "Observed current slot occupants and separate retained context records");
        var slotPrimary = new Grid { Margin = new Thickness(0, 8, 0, 0) };
        slotPrimary.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        slotPrimary.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        systemWorkStateText.FontWeight = FontWeights.SemiBold;
        systemWorkStateText.TextWrapping = TextWrapping.Wrap;
        systemWorkStateText.TextTrimming = TextTrimming.None;
        systemWorkStateText.VerticalAlignment = VerticalAlignment.Center;
        systemWorkStateText.Margin = new Thickness(2, 0, 6, 6);
        AutomationProperties.SetAutomationId(systemWorkStateText, "SystemWorkState");
        AutomationProperties.SetName(systemWorkStateText, "Current work hold and loaded local model state");
        var slotCaption = Ui.Text("", 11, Ui.Muted);
        slotCaption.SetBinding(TextBlock.TextProperty, new Binding(nameof(TextBlock.Text))
            { Source = systemSlotStatusText, Mode = BindingMode.OneWay });
        Grid.SetRow(slotCaption, 0); slotPrimary.Children.Add(slotCaption);
        Grid.SetRow(remoteSlotStatusText, 1);
        slotPrimary.Children.Add(remoteSlotStatusText);
        var detailsGroup = new StackPanel();
        // Keep the actual plugin controls and their callbacks. Only their presentation moves;
        // detailed counters and controls never participate in the bottom row's desired height.
        detailsGroup.Children.Add(systemWorkStateText);
        detailsGroup.Children.Add(primary);
        detailsGroup.Children.Add(slotPrimary);
        detailsGroup.Children.Add(resourceDetails);
        detailsGroup.Children.Add(slotDetails);
        var popup = new Popup { Placement = PlacementMode.Top, StaysOpen = false, AllowsTransparency = true };
        systemStatusDetailsPopup = popup;
        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
        var close = Ui.Button("Close", () => popup.IsOpen = false);
        close.Margin = new Thickness(8, 0, 0, 0); DockPanel.SetDock(close, Dock.Right); header.Children.Add(close);
        header.Children.Add(Ui.Text("System status", 14));
        detailsGroup.Children.Insert(0, header);
        var scroll = new ScrollViewer
        {
            Content = detailsGroup, MaxHeight = 360, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        };
        popup.Child = new Border
        {
            Child = scroll, Width = 640, Padding = new Thickness(12), Background = Ui.Paper,
            BorderBrush = Ui.Line, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(5)
        };
        AutomationProperties.SetAutomationId(popup.Child, "SystemStatusDetailsPopover");
        popup.Child.PreviewKeyDown += (_, args) =>
        { if (args.Key == Key.Escape) { popup.IsOpen = false; args.Handled = true; } };
        var detailsButton = ShellButton("Status ▴", "SystemStatusDetails", "Resource, slot and remote details, and work controls", () =>
        {
            ((FrameworkElement)popup.Child).Width = Math.Clamp(ActualWidth - 32, 280, 720);
            scroll.MaxHeight = Math.Clamp(ActualHeight - 96, 120, 420);
            popup.IsOpen = true;
        });
        detailsButton.MinHeight = 24; detailsButton.Height = double.NaN; detailsButton.Padding = new Thickness(7, 2, 7, 2);
        detailsButton.Margin = new Thickness(6, 0, 0, 0); detailsButton.VerticalAlignment = VerticalAlignment.Center;
        systemStatusDetailsButton = detailsButton; popup.PlacementTarget = detailsButton;
        desktopStatusItems.MaxHeight = 30;
        // Keep the existing guarded readouts visible; posture and full counters remain in Status.
        systemStatusText.Margin = new Thickness(0);
        inferenceOwnershipStatusText.Margin = new Thickness(0);
        var readouts = new StackPanel { VerticalAlignment = VerticalAlignment.Center, ClipToBounds = true };
        readouts.Children.Add(StatusMetricLine.CreateClippedView(systemStatusText));
        var activitySummary = new Grid { ClipToBounds = true };
        activitySummary.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3, GridUnitType.Star) });
        activitySummary.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star) });
        systemSlotStatusText.Margin = new Thickness(0, 0, 10, 0);
        activitySummary.Children.Add(systemSlotStatusText);
        Grid.SetColumn(inferenceOwnershipStatusText, 1); activitySummary.Children.Add(inferenceOwnershipStatusText);
        readouts.Children.Add(activitySummary);
        AutomationProperties.SetAutomationId(readouts, "SystemStatusReadouts");
        AutomationProperties.SetName(readouts, "Resources, model slots and observed activity");
        content.Children.Add(readouts);
        Grid.SetColumn(desktopStatusItems, 1); content.Children.Add(desktopStatusItems);
        Grid.SetColumn(detailsButton, 2); content.Children.Add(detailsButton);
        systemStatusBar = new Border
        {
            Child = content,
            Background = Ui.Paper,
            BorderBrush = Ui.Line,
            BorderThickness = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(6, 2, 6, 2),
            MinHeight = 32, ClipToBounds = true
        };
        AutomationProperties.SetAutomationId(systemStatusBar, "SystemStatusBar");
        ApplySystemStatusVisibility();
        InitializeInferenceOwnershipVisibility();
        return systemStatusBar;
    }

    private void CloseSystemStatusDetails()
    { if (systemStatusDetailsPopup is { } popup) popup.IsOpen = false; }

    private bool SystemStatusVisible => UiPreferenceStore.ReadBool("Cerebral.SystemStatusVisible", true);

    private void SetSystemStatusVisibility(bool visible)
    {
        UiPreferenceStore.WriteBool("Cerebral.SystemStatusVisible", visible);
        ApplySystemStatusVisibility();
        if (visible) _ = RefreshSystemStatusAsync();
    }

    private void ApplySystemStatusVisibility()
    {
        if (systemStatusBar is not null)
            systemStatusBar.Visibility = SystemStatusVisible ? Visibility.Visible : Visibility.Collapsed;
        if (!SystemStatusVisible) CloseSystemStatusDetails();
    }

    private InferenceOwnershipSample ReadStatusInferenceOwnershipSample(System.Text.Json.JsonElement wire, string installationId)
    {
        // Snapshot only the already-loaded profiles. A name is presentation, never an ownership or authority join.
        var cachedProfiles = profiles;
        var labels = cachedProfiles.Count <= SlotStatusPresentation.MaximumAgentLabels
            ? cachedProfiles.Select(profile => new SlotAgentLabel(profile.Id, profile.Revision, profile.Name)).ToArray()
            : Array.Empty<SlotAgentLabel>();
        return InferenceOwnershipProjection.ReadSample(wire, installationId, labels);
    }

    private Task RefreshSystemStatusAsync() => Task.WhenAll(
        RefreshSystemStatusAsync(SystemStatusVisible), RefreshSystemInferenceOwnershipAsync());

    private async Task RefreshSystemStatusAsync(bool statusVisible)
    {
        if (systemStatusRefreshActive || Lifetime.IsCancellationRequested) return;
        if (!statusVisible && agentSidebarView?.IsVisible != true)
        {
            ResetAgentSidebarSnapshot();
            return;
        }
        if (!IsConnected)
        {
            ResetAgentSidebarSnapshot();
            if (statusVisible) ShowSystemStatusMessage("Cerebral metrics unavailable while Cerebral is offline", ServiceStatusText);
            return;
        }

        var client = Client;
        var installationId = systemWorkMetricsInstallationId;
        if (installationId is null || !SystemWorkMetricsBindingCurrent(client, installationId))
        {
            ResetAgentSidebarSnapshot();
            return;
        }
        systemStatusRefreshActive = true;
        // Start exactly one execution observation independently of resource/throughput health.
        // The same task supplies the sidebar and (when visible) the status bar.
        var executionTask = ReadSystemExecutionMetricsAsync(client, installationId);
        // Remote reads are independent of local model and resource telemetry health.
        var remoteTask = statusVisible ? ReadRemoteSlotMetricsAsync(client, installationId) : null;
        // Optional hardware telemetry cannot gate the authoritative slot/model and hold readouts.
        // Start their existing bounded reads now, even if metrics.status fails or is unsupported.
        var workTask = statusVisible ? ReadSystemWorkMetricsAsync(client, installationId, executionTask, remoteTask!) : null;
        var holdTask = statusVisible ? ReadSystemHoldStatusAsync(client, installationId) : null;
        try
        {
            if (!statusVisible) { await executionTask; return; }
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(Lifetime.Token);
            deadline.CancelAfter(TimeSpan.FromSeconds(5));
            var metrics = await client.GetAsync<DesktopStatusMetrics>("metrics.status",
                new { desktopProcessId = Environment.ProcessId }, cancellationToken: deadline.Token);
            var work = await workTask!;
            var hold = await holdTask!;
            var resources = ProjectSystemResources(metrics, work);
            var tokens = ProjectTokenUsage(metrics.Tokens);
            GenerationThroughputPresentation throughput;
            try
            {
                throughput = GenerationThroughput.Project(await client.GetAsync<System.Text.Json.JsonElement>("metrics.generation",
                    cancellationToken: deadline.Token));
            }
            catch (OperationCanceledException) when (Lifetime.IsCancellationRequested) { throw; }
            catch (Exception)
            { throughput = new("—", "—", 0, 0, "Live generation throughput is unavailable from this Worker."); }
            // A reconnect during any later observation must not publish counters from the previous owner.
            if (!SystemWorkMetricsBindingCurrent(client, installationId)) return;
            ApplySystemWorkStatus(work, hold);
            systemSlotStatusText.Text = "Local: " + (resources.Values.ElementAtOrDefault(4) ?? SlotStatusPresentation.UnknownAllocatorCaption) +
                "  ·  Queued " + (resources.Values.ElementAtOrDefault(5) ?? "—");
            systemSlotStatusText.ToolTip = resources.Details;
            remoteSlotStatusText.Text = work.RemoteSlots;
            remoteSlotStatusText.ToolTip = work.RemoteDetails;
            var tokenValue = $"Total {throughput.Input} in / {throughput.Output} out tok/s";
            var tokenDetail = throughput.Details + "\n\n" + $"Confirmed totals: {tokens.Input} in / {tokens.Output} out\n" + tokens.Details;
            var details = resources.Details + "\n\n" + tokens.Details;
            if (systemMetricLine is not null)
                systemMetricLine.SetValues(resources.Values, details);
            else
            {
                var labels = new[] { "CPU", "RAM", "GPU", "VRAM", "Slots", "Queued" };
                systemStatusText.Text = string.Join("  ·  ", labels.Zip(resources.Values, (label, value) => label + " " + value));
                systemStatusText.ToolTip = details;
            }
            if (tokenMetricLine is not null)
                tokenMetricLine.SetValues([tokenValue], tokenDetail);
            else
            {
                systemTokenStatusText.Text = tokenValue;
                systemTokenStatusText.ToolTip = tokenDetail;
            }
        }
        catch (OperationCanceledException) when (Lifetime.IsCancellationRequested) { }
        catch (Exception error) when (error is not (OutOfMemoryException or StackOverflowException or AccessViolationException))
        {
            if (statusVisible && SystemWorkMetricsBindingCurrent(client, installationId))
            {
                ShowSystemStatusMessage("Cerebral metrics temporarily unavailable", error.Message);
                // These observations remain useful when CPU/GPU/token telemetry is unavailable.
                try
                {
                    var work = await workTask!;
                    var hold = await holdTask!;
                    if (SystemWorkMetricsBindingCurrent(client, installationId)) ApplySystemWorkStatus(work, hold);
                }
                catch (OperationCanceledException) when (Lifetime.IsCancellationRequested) { }
                catch (Exception observationError) when (observationError is not (OutOfMemoryException or StackOverflowException or AccessViolationException))
                { /* The visible unavailable state remains; a failed optional observation cannot stop refreshes. */ }
            }
        }
        finally
        {
            // Observe every already-started bounded read and always release the refresh guard.
            // Otherwise one failed observation can strand the entire bottom bar on an old sample.
            var reads = new List<Task> { executionTask };
            if (remoteTask is not null) reads.Add(remoteTask);
            if (workTask is not null) reads.Add(workTask);
            if (holdTask is not null) reads.Add(holdTask);
            try { await Task.WhenAll(reads); }
            catch (OperationCanceledException) when (Lifetime.IsCancellationRequested) { }
            catch (Exception observationError) when (observationError is not (OutOfMemoryException or StackOverflowException or AccessViolationException)) { }
            finally { systemStatusRefreshActive = false; }
        }
    }

    private async Task<SystemStatusPosture.Hold?> ReadSystemHoldStatusAsync(CerebralClient client, string installationId)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(Lifetime.Token);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            var wire = await client.GetAsync<System.Text.Json.JsonElement>("engine.work-hold.status", new { }, cancellationToken: deadline.Token);
            return SystemWorkMetricsBindingCurrent(client, installationId) ? SystemStatusPosture.ReadHold(wire) : null;
        }
        catch (OperationCanceledException) when (Lifetime.IsCancellationRequested) { throw; }
        catch (Exception error) when (error is not (OutOfMemoryException or StackOverflowException or AccessViolationException)) { return null; }
    }

    private void ApplySystemWorkStatus(SystemWorkPresentation work, SystemStatusPosture.Hold? hold)
    {
        var presentation = SystemStatusPosture.Show(hold, work.LoadedModels);
        systemWorkStateText.Text = presentation.Caption;
        systemWorkStateText.ToolTip = presentation.Details;
        systemWorkStateText.SetValue(AutomationProperties.HelpTextProperty, presentation.Details);
        systemSlotStatusText.Text = "Local: " + work.OpenSlots;
        systemSlotStatusText.ToolTip = work.Details;
        remoteSlotStatusText.Text = work.RemoteSlots;
        remoteSlotStatusText.ToolTip = work.RemoteDetails;
    }

    private void ShowSystemStatusMessage(string message, string detail)
    {
        var unavailable = SystemStatusPosture.Show(null, null);
        systemWorkStateText.Text = unavailable.Caption;
        systemWorkStateText.ToolTip = message + "\n" + detail;
        var remote = RemoteSlotProjection.Show(remoteSlotRegistrations, remoteSlotObservations,
            DateTimeOffset.UtcNow, IsConnected);
        systemSlotStatusText.Text = "Local: " + SlotStatusPresentation.UnknownAllocatorCaption;
        systemSlotStatusText.ToolTip = message + "\n" + detail;
        remoteSlotStatusText.Text = remote.Caption;
        remoteSlotStatusText.ToolTip = remote.Details;
        if (systemMetricLine is not null) systemMetricLine.ShowMessage(message, detail);
        else { systemStatusText.Text = message; systemStatusText.ToolTip = detail; }
        if (tokenMetricLine is not null) tokenMetricLine.ShowMessage("Tokens — in / — out", detail);
        else { systemTokenStatusText.Text = "Tokens — in / — out"; systemTokenStatusText.ToolTip = detail; }
    }

    private static double Gib(long mib) => mib / 1024d;

    private static GpuStatusPresentation ProjectGpuStatus(DesktopStatusMetrics metrics)
    {
        var devices = metrics.Resources.Gpus.Where(item =>
            item.TotalMb is not null || item.UsedMb is not null || item.FreeMb is not null || item.UtilizationPercent is not null).ToList();
        var utilization = devices.Where(item => item.UtilizationPercent is >= 0 and <= 100).Select(item => item.UtilizationPercent!.Value).ToList();
        var utilizationText = utilization.Count == 0 ? "—" :
            utilization.Max().ToString(CultureInfo.InvariantCulture) + "%" + (devices.Count > 1 ? " max" : "") +
            (utilization.Count < metrics.Resources.Gpus.Count ? " observed" : "");
        var memoryDevices = devices.Select(item => new
        {
            Device = item,
            Used = item.UsedMb ?? (item.TotalMb is > 0 && item.FreeMb is >= 0 && item.FreeMb <= item.TotalMb
                ? item.TotalMb.Value - item.FreeMb.Value : (long?)null)
        }).Where(item => item.Device.TotalMb is > 0 && item.Used is >= 0 && item.Used <= item.Device.TotalMb).ToList();
        var memoryText = memoryDevices.Count == 0 ? "—" :
            Gib(memoryDevices.Sum(item => item.Used!.Value)).ToString("0.0", CultureInfo.InvariantCulture) + "/" +
            Gib(memoryDevices.Sum(item => item.Device.TotalMb!.Value)).ToString("0.0", CultureInfo.InvariantCulture) + " GiB" +
            (memoryDevices.Count < metrics.Resources.Gpus.Count ? " observed" : "");
        var details = devices.Count == 0
            ? "GPU device telemetry is unavailable on this Worker."
            : string.Join("\n", devices.Select(item =>
            {
                var used = item.UsedMb ?? (item.TotalMb is { } total && item.FreeMb is { } free ? Math.Max(0, total - free) : (long?)null);
                var memory = used is { } usedMb && item.TotalMb is { } totalMb
                    ? $"{usedMb:N0} / {totalMb:N0} MiB used"
                    : "VRAM unavailable";
                var activity = item.UtilizationPercent is { } percent ? percent + "% utilization" : "utilization unavailable";
                var shared = item.Unified ? " · unified CPU/GPU memory" : "";
                var error = string.IsNullOrWhiteSpace(item.Error) ? "" : " · " + item.Error;
                return $"{item.Name}: {activity} · {memory}{shared}{error}";
            }));
        return new(utilizationText, memoryText, details);
    }

    private static string FormatBytes(long bytes)
    {
        var gib = bytes / 1024d / 1024d / 1024d;
        return gib >= 1024 ? (gib / 1024d).ToString("0.0", CultureInfo.InvariantCulture) + " TiB" :
            gib.ToString("0.0", CultureInfo.InvariantCulture) + " GiB";
    }

    private static string Compact(long value) => value switch
    {
        >= 1_000_000_000 => (value / 1_000_000_000d).ToString("0.#", CultureInfo.InvariantCulture) + "B",
        >= 1_000_000 => (value / 1_000_000d).ToString("0.#", CultureInfo.InvariantCulture) + "M",
        >= 1_000 => (value / 1_000d).ToString("0.#", CultureInfo.InvariantCulture) + "K",
        _ => value.ToString(CultureInfo.InvariantCulture)
    };

    private static TokenUsagePresentation ProjectTokenUsage(ModelUsageTotals totals)
    {
        static string Project(long value, bool? complete) => complete == false
            ? value > 0 ? Compact(value) + "+" : "—"
            : Compact(value);

        var details = "Token totals include only evaluated-input and generated-output usage confirmed by local or paired model runtimes since this Worker started.";
        if (totals.ConfirmedInputComplete == false || totals.ConfirmedOutputComplete == false)
            details += " A + marks a confirmed lower bound because some provider usage was unavailable; — means no count was confirmed for that incomplete side.";
        else if (totals.ConfirmedInputComplete is null || totals.ConfirmedOutputComplete is null)
            details += " This Worker did not report whether these totals are complete.";
        return new(Project(totals.ConfirmedInputTokens, totals.ConfirmedInputComplete),
            Project(totals.ConfirmedOutputTokens, totals.ConfirmedOutputComplete), details);
    }

    private sealed record GpuStatusPresentation(string Utilization, string Memory, string Details);
    private sealed record TokenUsagePresentation(string Input, string Output, string Details);

    private void MountDesktopStatusItems()
    {
        ClearPrimaryStatusControls();
        foreach (var frame in desktopStatusItems.Children.OfType<Border>().ToList()) frame.Child = null;
        desktopStatusItems.Children.Clear();
        if (ConnectorStartupWarning(connectorStartup.Outcome) is { } connectorWarning) desktopStatusItems.Children.Add(connectorWarning);
        if (desktopPlugins is null) return;
        foreach (var item in desktopPlugins.StatusItems)
        {
            if (TryMountPrimaryStatusControl(item)) continue;
            if (item.View.Parent is ContentControl old) old.Content = null;
            else if (item.View.Parent is Border oldBorder) oldBorder.Child = null;
            var frame = DesktopMountLayout.StatusItemFrame(item.View);
            frame.ToolTip = item.Title;
            AutomationProperties.SetAutomationId(frame, "PluginStatusItem-" + item.PluginId + "-" + item.Id);
            AutomationProperties.SetName(frame, item.Title + " plugin status item");
            desktopStatusItems.Children.Add(frame);
        }
    }

    internal static TextBlock? ConnectorStartupWarning(MainConnectorStartupResult? outcome)
    {
        if (outcome is not { Warning: true }) return null;
        var warning = Ui.Text("Connector startup warning", 11, Ui.ErrorText);
        warning.Margin = new Thickness(5, 0, 0, 0);
        warning.ToolTip = outcome.Message;
        AutomationProperties.SetAutomationId(warning, "UserConnectorStartupWarning");
        AutomationProperties.SetName(warning, "Optional user connector startup was not confirmed");
        return warning;
    }
}
