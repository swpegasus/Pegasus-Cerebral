using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Cerebral.Core;
using Cerebral.Desktop;
using Cerebral.Desktop.Plugins;

namespace Cerebral.Orchestrator;

public sealed partial class OrchestratorWindow
{
    /// <summary>Deterministic telemetry and native offscreen mount checks. No connection or plugin command.</summary>
    public static int CheckSystemResourceUi(string? previewPath = null)
    {
        var checks = 0;
        void Check(bool value, string reason) { if (!value) throw new InvalidOperationException(reason); checks++; }
        var heldWire = System.Text.Json.JsonSerializer.SerializeToElement(new
        {
            contract = "cerebral.owner-work-hold/1", state = "held", holdId = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            generation = 1, revision = 2, engineEpoch = 3, enginePaused = true
        });
        var held = SystemStatusPosture.ReadHold(heldWire);
        var heldEmpty = SystemStatusPosture.Show(held, 0);
        Check(heldEmpty.Caption == "Work hold active · No local model loaded" && heldEmpty.Details.Contains("queued work", StringComparison.Ordinal),
            "Verified hold and complete empty model observations explain why queued work is idle without changing either state.");
        Check(SystemStatusPosture.Show(new(false, true, "cancelled"), 1).Caption == "Engine paused · 1 local model loaded",
            "An engine pause is separate from an active intentional hold.");
        Check(SystemStatusPosture.Show(null, null).Caption == "Hold status unavailable · Local model status unavailable",
            "Missing telemetry remains unknown rather than being presented as no hold or no loaded model.");
        Check(SystemStatusPosture.ReadHold(System.Text.Json.JsonSerializer.SerializeToElement(new { contract = "future", state = "held" })) is null,
            "An unsupported hold contract cannot claim a known state.");
        var stamp = DateTimeOffset.Parse("2026-10-12T21:00:00Z", System.Globalization.CultureInfo.InvariantCulture);
        var fixture = new DesktopStatusMetrics(new(512, 48 * 1024,
            [new("Fixture GPU", 8 * 1024, 6 * 1024, false, UtilizationPercent: 28)])
            { SystemTotalMb = 64 * 1024, CpuUtilizationPercent = 24 }, 3, 2, 3, new(10, 20), stamp)
            { Cerebral = new(2 * 1024, 6, 1024, 3, 64L * 1024 * 1024 * 1024, null, "fixture") };
        var projection = ProjectSystemResources(fixture);
        Check(projection.Values.Take(4).SequenceEqual(new[] {
            "6% Cerebral / 24% total", "2.0 Cerebral / 16.0/64.0 GiB total",
            "— Cerebral / 28% total", "1.0 GiB Cerebral / 2.0/8.0 GiB total" }),
            "Resource values retain CPU/RAM/GPU/VRAM order and exact owned/machine attribution.");
        Check(projection.Values[4] == SlotStatusPresentation.UnknownAllocatorCaption && projection.Values[5] == "2",
            "Assignments and queued work must not fabricate managed slot availability.");
        var legacy = ProjectSystemResources(fixture with { Cerebral = null });
        Check(legacy.Values[0].StartsWith("— Cerebral", StringComparison.Ordinal) && legacy.Values[1].StartsWith("— Cerebral", StringComparison.Ordinal),
            "A legacy Worker process memory value is not evidence for combined Cerebral ownership.");
        var invalid = ProjectSystemResources(fixture with { Resources = fixture.Resources with
            { CpuUtilizationPercent = double.NaN, SystemAvailableMb = 100 * 1024, Gpus = [new("Invalid GPU", 0, -1, false, UtilizationPercent: 200)] } });
        Check(invalid.Values[0].EndsWith("/ — total", StringComparison.Ordinal) && invalid.Values[1].Contains("/ —/64.0", StringComparison.Ordinal) &&
            invalid.Values[2].Contains("/ — total", StringComparison.Ordinal) && invalid.Values[3].EndsWith("/ — total", StringComparison.Ordinal),
            "Malformed or unavailable machine observations remain unknown instead of being clamped to success.");
        var partial = ProjectSystemResources(fixture with { Resources = fixture.Resources with
            { Gpus = [fixture.Resources.Gpus[0], new("Missing device", null, null, false)] } });
        Check(partial.Values[2].Contains("observed", StringComparison.Ordinal) && partial.Values[3].Contains("observed", StringComparison.Ordinal),
            "Incomplete multi-device telemetry is labeled as observed coverage.");
        var unified = ProjectSystemResources(fixture with { Resources = fixture.Resources with
            { Gpus = [fixture.Resources.Gpus[0] with { Unified = true }] } });
        Check(unified.Details.Contains("unified CPU/GPU memory", StringComparison.Ordinal) && unified.Values[1] == projection.Values[1],
            "Unified GPU memory is identified and never added to system RAM capacity.");
        const string installation = "11111111111111111111111111111111";
        System.Text.Json.JsonElement Work(int observed=2,bool partial=false,int? complete=2,string issuer=installation) => CerebralJson.Element(new
        {
            schema="cerebral.execution-metrics/1",issuingInstallationId=issuer,processGenerationId="22222222222222222222222222222222",
            processGenerationScope="issuing-worker-scheduler-instance; not ModelRuntime",sampledAt=stamp,state=partial?"partial":"observed",
            partial,truncated=partial,unprojectableCount=0,ownershipChangedCount=0,observedLocalWorkingAgentCount=observed,
            observedLocalExecutionCount=3,capturedOwnershipCount=3,localWorkingAgentCount=complete,confirmedRemoteWorkingAgentCount=(int?)null
        });
        var observedWork=ProjectExecutionStatus(Work(),installation);
        Check(observedWork.Agents=="2 local / — remote" && observedWork.OpenSlots=="—" && observedWork.Details.Contains("contexts: 3",StringComparison.Ordinal),
            "Observed local unique agents are distinct from execution contexts, remote uncertainty and unmeasured slots.");
        Check(ProjectExecutionStatus(Work(partial:true,complete:null),installation).Agents=="≥2 local / — remote",
            "Partial local ownership observation exposes only an explicit lower bound.");
        Check(ProjectExecutionStatus(Work(partial:true,complete:2),installation).Agents.StartsWith("—",StringComparison.Ordinal),
            "A contradictory complete count in a partial sample remains unavailable.");
        Check(ProjectExecutionStatus(Work(issuer:"33333333333333333333333333333333"),installation).Agents.StartsWith("—",StringComparison.Ordinal),
            "An observation from another Worker cannot enter the selected owner's status bar.");
        Check(ProjectExecutionStatus(Work(observed:4,complete:4),installation).Agents.StartsWith("—",StringComparison.Ordinal),
            "Unique agent counts cannot exceed the independently observed execution count.");
        var observedResources = ProjectSystemResources(fixture, observedWork with
            { OpenSlots = "3 free / 6 total / 2 quarantined", Details = observedWork.Details + "\nDeterministic observed managed-slot fixture." });
        Check(observedResources.Values[4] == "3 free / 6 total / 2 quarantined" && observedResources.Values[5] == "2",
            "The native readout presents measured allocator slots and queue while agent counts remain in details.");

        var root = Path.Combine(Path.GetTempPath(), "cerebral-status-fixture-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var window = new OrchestratorWindow(new DesktopPluginCatalog(Path.Combine(root, "plugins")), root, Path.Combine(root, "drafts"));
        try
        {
            ClearPrimary(window);
            var buttons = new[] { ("resource-status", "Local GPU · used"), ("engine-status", "Engine A"), ("work-hold-status", "Hold work") };
            foreach (var (id, title) in buttons)
                Check(window.TryMountPrimaryStatusControl(new("pegasus.resource-pause", id, title, Ui.Button(title, () =>
                    throw new InvalidOperationException("This renderer fixture must never dispatch a control.")))), "The exact primary control is routed to the left group: " + id);
            Check(!window.TryMountPrimaryStatusControl(new("unrelated.plugin", "engine-status", "Other", new Border())),
                "Only the owner plugin's three exact IDs are treated as primary controls.");
            var bar = window.systemStatusBar ?? throw new InvalidOperationException("Missing status bar fixture.");
            bar.VerticalAlignment = VerticalAlignment.Top;
            if (bar.Parent is Panel parent) parent.Children.Remove(bar);
            window.systemMetricLine!.SetValues(observedResources.Values, observedResources.Details);
            window.tokenMetricLine!.SetValues(["Total — in / 42 out tok/s · fixture"], "Deterministic fixture; no live model throughput.");
            var frame = new Border { Background = Ui.Surface, Padding = new Thickness(12), Child = bar };
            using var source = new HwndSource(new HwndSourceParameters("Cerebral status renderer fixture")
            { Width = 1240, Height = 160, PositionX = -32000, PositionY = -32000, WindowStyle = unchecked((int)0x80000000) }) { RootVisual = frame };
            frame.Measure(new Size(1240,160)); frame.Arrange(new Rect(0,0,1240,160));frame.UpdateLayout();
            var content = (Grid)bar.Child;
            var readouts = content.Children.OfType<StackPanel>().Single();
            var activitySummary = readouts.Children.OfType<Grid>().Single(grid => grid.Children.Contains(window.systemSlotStatusText));
            var resourceView = readouts.Children.OfType<Grid>().Single(grid => grid.Children.Contains(window.systemStatusText));
            var popup = window.systemStatusDetailsPopup ?? throw new InvalidOperationException("Missing status details popover.");
            var popupFrame = (Border)popup.Child;
            var detailsGroup = (StackPanel)((ScrollViewer)popupFrame.Child).Content;
            Check(window.desktopPrimaryStatusItems.Parent == detailsGroup && window.desktopPrimaryStatusItems.Children.Count == 3 &&
                window.desktopPrimaryStatusItems.Children.OfType<Border>().Select(Grid.GetColumn).Order().SequenceEqual(new[] { 0, 1, 2 }),
                "The three actual plugin controls retain their stable cells inside the popover.");
            Check(window.systemSlotStatusText.Text.StartsWith("Local: ", StringComparison.Ordinal) &&
                activitySummary.Children.Contains(window.inferenceOwnershipStatusText) &&
                detailsGroup.Children.OfType<Grid>().Any(grid => grid.Children.Contains(window.remoteSlotStatusText)) &&
                ReferenceEquals(window.systemWorkStateText.Parent, detailsGroup),
                "The bottom-left row retains actual local slots and activity; full work posture and remote details stay in Status.");
            Check(!popup.StaysOpen && !popup.IsOpen && popup.PlacementTarget == window.systemStatusDetailsButton,
                "Details use an initially closed, outside-click-dismissed popover anchored to the status button.");
            var expectedResourceSummary = string.Join(" · ", new[] { "CPU", "RAM", "GPU", "VRAM", "Slots", "Queued" }
                .Zip(observedResources.Values, (label, value) => (label + " " + value).Trim()));
            var expectedResourceTooltip = expectedResourceSummary + "\n\n" + observedResources.Details +
                (StatusMetricIcons.Diagnostic is { } diagnostic ? "\n\n" + diagnostic : "");
            Check(window.systemStatusText.TextWrapping == TextWrapping.NoWrap && resourceView.ClipToBounds &&
                AutomationProperties.GetName(window.systemStatusText) == expectedResourceSummary &&
                Equals(window.systemStatusText.ToolTip, expectedResourceTooltip),
                "Native CPU/RAM/GPU/VRAM/slot readouts retain their complete tooltip and accessible summary inside a bounded row.");
            Check(content.RowDefinitions.Count == 1 && content.RowDefinitions[0].Height.IsAuto && content.Children.Count == 3 &&
                content.Children.Contains(readouts) && content.Children.Contains(window.desktopStatusItems) &&
                !content.Children.Contains(window.systemWorkStateText) && !content.Children.Contains(window.desktopPrimaryStatusItems) &&
                !content.Children.Contains(detailsGroup) && double.IsNaN(bar.Height) && double.IsPositiveInfinity(bar.MaxHeight),
                "Resources and activity share an auto-height left group beside plugins and Status, without a second posture row or fixed-height clipping.");
            var preservedControls = window.desktopPrimaryStatusItems.Children.OfType<Border>().Select(border => border.Child).ToArray();
            var pluginTool = new Button { Content = "AI Dictation Auto", Height = 24, Margin = new Thickness(0) };
            var pluginFrame = DesktopMountLayout.StatusItemFrame(pluginTool);
            window.desktopStatusItems.Children.Add(pluginFrame);
            frame.UpdateLayout();
            double Left(FrameworkElement element) => element.TransformToAncestor(frame).Transform(new Point()).X;
            var fixedPositions = new[] { Left(readouts), Left(window.desktopStatusItems), Left(window.systemStatusDetailsButton!) };
            window.systemSlotStatusText.Text = "Local: — free";
            window.remoteSlotStatusText.Text = "Remote: — free";
            frame.UpdateLayout();
            window.systemSlotStatusText.Text = "Local: 16 free / 32 total / 8 quarantined · workflow closed";
            window.remoteSlotStatusText.Text = "Remote: ≥120 observed free · 8 online, 12 unknown";
            foreach (var button in window.desktopPrimaryStatusItems.Children.OfType<Border>()
                         .Select(border => border.Child).OfType<Button>())
            {
                button.Content = "Long changing plugin status and a measured value 999,999";
                button.ToolTip = "Full changing plugin status and measured value 999,999";
            }
            frame.UpdateLayout();
            popupFrame.Measure(new Size(640, 420)); popupFrame.Arrange(new Rect(0, 0, 640, 420));
            frame.UpdateLayout();
            Check(new[] { Left(readouts), Left(window.desktopStatusItems), Left(window.systemStatusDetailsButton!) }
                .Zip(fixedPositions, (current, initial) => Math.Abs(current - initial) < 1).All(equal => equal) &&
                window.desktopPrimaryStatusItems.Children.OfType<Border>().Select(border => border.Child).SequenceEqual(preservedControls),
                "Detailed telemetry and long plugin captions preserve bottom-group positions and actual control instances.");
            Check(window.desktopPrimaryStatusItems.Children.OfType<Border>().All(border =>
                    Equals(border.ToolTip, "Full changing plugin status and measured value 999,999")),
                "The complete changing plugin status remains available through its frame tooltip.");
            foreach (var width in new[] { 720d, 900d, 1420d })
            {
                frame.Width = width; frame.Height = 160;
                frame.Measure(new Size(width, 160)); frame.Arrange(new Rect(0, 0, width, 160)); frame.UpdateLayout();
                Check(bar.ActualHeight >= 32 && bar.ActualHeight + .5 >= content.DesiredSize.Height + bar.Padding.Top + bar.Padding.Bottom &&
                    readouts.ActualWidth > 0 && resourceView.ActualWidth <= readouts.ActualWidth + .5 && activitySummary.ClipToBounds &&
                    window.systemWorkStateText.TextWrapping == TextWrapping.Wrap &&
                    ReferenceEquals(pluginFrame.Child, pluginTool) && pluginFrame.ActualHeight <= 30.5,
                    "Resource and activity rows size to their text while the existing plugin mount stays bounded at viewport " + width + ".");
                Check(Left(readouts) <= Left(content) + 4 &&
                    Left(window.systemStatusDetailsButton!) + window.systemStatusDetailsButton!.ActualWidth <= Left(bar) + bar.ActualWidth + .5,
                    "There is no reserved blank-left area and the details action remains in bounds.");
            }
            var normalStatusHeight = bar.ActualHeight;
            var originalReadoutFonts = new[] { window.systemStatusText.FontSize, window.systemSlotStatusText.FontSize,
                window.inferenceOwnershipStatusText.FontSize, window.systemStatusDetailsButton!.FontSize };
            try
            {
                window.systemStatusText.FontSize = window.systemSlotStatusText.FontSize = window.inferenceOwnershipStatusText.FontSize = 18;
                window.systemStatusDetailsButton.FontSize = 18;
                frame.Measure(new Size(1420, 160)); frame.Arrange(new Rect(0, 0, 1420, 160)); frame.UpdateLayout();
                Check(bar.ActualHeight > normalStatusHeight && bar.ActualHeight + .5 >= bar.DesiredSize.Height &&
                    window.systemStatusDetailsButton.ActualHeight >= window.systemStatusDetailsButton.DesiredSize.Height - .5 &&
                    Left(readouts) + readouts.ActualWidth <= Left(window.desktopStatusItems) + .5,
                    "Larger status fonts grow the bar instead of cropping text or overlapping the plugin group.");
            }
            finally
            {
                window.systemStatusText.FontSize = originalReadoutFonts[0]; window.systemSlotStatusText.FontSize = originalReadoutFonts[1];
                window.inferenceOwnershipStatusText.FontSize = originalReadoutFonts[2]; window.systemStatusDetailsButton!.FontSize = originalReadoutFonts[3];
            }
            Check(!popup.IsOpen, "Layout and telemetry refresh never open the popover or request focus.");
            frame.Width = 1240; frame.Height = 160;
            frame.Measure(new Size(1240, 160)); frame.Arrange(new Rect(0, 0, 1240, 160)); frame.UpdateLayout();
            if (previewPath is not null)
            {
                var bitmap = new RenderTargetBitmap(1240,160,96,96,PixelFormats.Pbgra32);bitmap.Render(frame);
                var encoder = new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var file = new FileStream(previewPath, FileMode.CreateNew, FileAccess.Write);encoder.Save(file);
            }
            source.RootVisual = null;frame.Child = null;
            window.ClearPrimaryStatusControls();
            Check(window.desktopPrimaryStatusItems.Children.Count == 0 && window.desktopPrimaryStatusItems.Visibility == Visibility.Collapsed,
                "Clearing the primary group detaches views and preserves remount capability.");
        }
        finally
        {
            window.Close();
            if (Path.GetDirectoryName(root) != Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) ||
                !Path.GetFileName(root).StartsWith("cerebral-status-fixture-", StringComparison.Ordinal)) throw new InvalidOperationException("Invalid fixture cleanup root.");
            Directory.Delete(root, true);
        }
        return checks;
        static void ClearPrimary(OrchestratorWindow target) => target.ClearPrimaryStatusControls();
    }
}
