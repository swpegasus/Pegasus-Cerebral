using System.IO;
using System.Reflection;
using Cerebral.Client;
using Cerebral.Contracts;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Cerebral.Core;
using Cerebral.Desktop;

namespace Cerebral.Orchestrator;

public sealed partial class OrchestratorWindow
{
    // Deterministic layout acceptance; it does not show a window, connect to a service or launch Configuration.
    public int CheckHeaderUi(string? previewPath = null)
    {
        var checks = 0;
        void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
            checks++;
        }
        static FrameworkElement Find(DependencyObject parent, string id)
        {
            for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
            {
                var child = VisualTreeHelper.GetChild(parent, index);
                if (child is FrameworkElement element && AutomationProperties.GetAutomationId(element) == id) return element;
                try { return Find(child, id); } catch (InvalidOperationException) { }
            }
            throw new InvalidOperationException("Missing header control: " + id);
        }
        static Rect Bounds(FrameworkElement element, FrameworkElement ancestor)
        {
            var origin = element.TransformToAncestor(ancestor).Transform(new Point());
            return new Rect(origin, new Size(element.ActualWidth, element.ActualHeight));
        }
        static double Rounded(double value) => Math.Round(value, 2);

        try
        {
            var container = (FrameworkElement)Content;
            // Realize templated scroll content before strict visual-tree inspection in this unshown fixture.
            container.Measure(new Size(1280, 840)); container.Arrange(new Rect(0, 0, 1280, 840)); container.UpdateLayout();
            var header = Find(container, "ApplicationHeader");
            var ownership = Find(container, "ConnectionOwnership");
            var navigation = Find(container, "ToggleWorkspaceSidebar");
            var panels = Find(container, "ToggleActivitySidebar");
            var reconnect = (Button)Find(container, "ReconnectService");
            var configuration = (Button)Find(container, "OpenConfiguration");
            var tabs = (TabControl)Find(container, "ApplicationTabs");
            var systemStatus = Find(container, "SystemStatusBar");
            var systemStatusMetrics = Find(container, "SystemStatusMetrics");
            var systemReadouts = Find(container, "SystemStatusReadouts");
            var statusDetails = Find(container, "SystemStatusDetails");
            var connectionStatus = Find(container, "ConnectionStatus");
            Check(ReferenceEquals(connectionStatus, Status) &&
                Find(container, "ConnectionAttentionText") is ScrollViewer statusScroll && ReferenceEquals(statusScroll.Content, Status),
                "The realized connection notice retains the original status control, automation identity and scroll content.");
            Check(Title == "Pegasus Cerebral — " + sessionHeading.Text,
                "The OS title includes the current chat name through the existing heading binding.");
            var originalHeading = sessionHeading.Text;
            sessionHeading.Text = "Controlled header title"; GetBindingExpression(TitleProperty)!.UpdateTarget();
            Check(Title == "Pegasus Cerebral — Controlled header title" && !sessionHeading.IsDescendantOf(container),
                "Chat selection/title setters update the OS title without repeating the chat title beside Main.");
            sessionHeading.Text = "Controlled renamed title"; GetBindingExpression(TitleProperty)!.UpdateTarget();
            Check(Title == "Pegasus Cerebral — Controlled renamed title", "A chat rename updates the same OS title binding.");
            sessionHeading.Text = originalHeading; GetBindingExpression(TitleProperty)!.UpdateTarget();
            Check(mainWorkPanel is not null && conversationHeaderStrip!.IsDescendantOf(mainWorkPanel) &&
                !sessionHeading.IsDescendantOf(mainWorkPanel),
                "The existing Main collapse header retains status and actions without a second chat title.");
            var notice = CaptureBannerNotice();
            Check(SameBannerNotice(notice, CaptureBannerNotice()) &&
                !SameBannerNotice(notice, notice with { Publication = notice.Publication + 1 }) &&
                !SameBannerNotice(notice, notice with { ConversationId = "different-owned-fixture-chat" }) &&
                !SameBannerNotice(notice, notice with { Selection = notice.Selection + 1 }) &&
                !SameBannerNotice(notice, notice with { Version = notice.Version + 1 }) &&
                !SameBannerNotice(notice, notice with { RunId = "different-owned-fixture-run" }) &&
                !SameBannerNotice(notice, notice with { RunRevision = (notice.RunRevision ?? 0) + 1 }) &&
                !SameBannerNotice(notice, notice with { EffectId = "different-owned-fixture-effect" }) &&
                !SameBannerNotice(notice, notice with { Connection = new object() }),
                "UI dismissal is fenced to the exact diagnostic publication, owner, chat, selection, run and effect.");
            checks += CheckScopedAttentionBannerUi();
            var gpuProjection = ProjectGpuStatus(new DesktopStatusMetrics(
                new ResourceInfo(512, 24_000, [new GpuInfo("Fixture GPU", 16_384, 12_288, false, UtilizationPercent: 37) { UsedMb = 4_096 }]),
                1, 2, 0, new(100, 20), DateTimeOffset.UtcNow)
            {
                Cerebral = new(640, 4.2, null, 3, null, null, "fixture")
                {
                    GpuMemoryUnavailableReason = "Fixture process VRAM unavailable."
                }
            });
            Check(gpuProjection.Utilization == "37%" && gpuProjection.Memory == "4.0/16.0 GiB" &&
                  gpuProjection.Details.Contains("Fixture GPU", StringComparison.Ordinal),
                "The bottom status projection must show device utilization and used/total VRAM independently of optional process attribution.");
            var unavailableGpu = ProjectGpuStatus(new DesktopStatusMetrics(new ResourceInfo(128, 1_024,
                [new GpuInfo("GPU probe unavailable", null, null, false, "fixture unavailable")]), 0, 0, 0, new(0, 0), DateTimeOffset.UtcNow));
            Check(unavailableGpu.Utilization == "—" && unavailableGpu.Memory == "—",
                "Unavailable GPU telemetry must degrade explicitly without inventing zero utilization or VRAM.");
            var completeTokens = ProjectTokenUsage(new(0, 0)
                { ConfirmedInputComplete = true, ConfirmedOutputComplete = true });
            Check(completeTokens.Input == "0" && completeTokens.Output == "0" &&
                  !completeTokens.Details.Contains("lower bound", StringComparison.Ordinal),
                "An explicitly complete zero-token total remains a real zero without a lower-bound warning.");
            var partialTokens = ProjectTokenUsage(new(64_138, 1_514)
                { ConfirmedInputComplete = false, ConfirmedOutputComplete = true });
            Check(partialTokens.Input == "64.1K+" && partialTokens.Output == "1.5K" &&
                  partialTokens.Details.Contains("lower bound", StringComparison.Ordinal) &&
                  partialTokens.Details.Contains("paired", StringComparison.Ordinal),
                "A partially unavailable paired-runtime total retains its confirmed lower bound and explains the incomplete side.");
            var unavailableTokens = ProjectTokenUsage(new(0, 0)
                { ConfirmedInputComplete = false, ConfirmedOutputComplete = false });
            Check(unavailableTokens.Input == "—" && unavailableTokens.Output == "—",
                "Unavailable token usage must not be rendered as a confirmed zero.");
            var legacyTokens = ProjectTokenUsage(new(1_000, 2_000));
            Check(legacyTokens.Input == "1K" && legacyTokens.Output == "2K" &&
                  legacyTokens.Details.Contains("did not report", StringComparison.Ordinal),
                "An older Worker without completeness flags retains its numeric status compatibility with an explicit tooltip qualification.");

            void Arrange(double width, double height)
            {
                var size = new Size(width, height);
                container.Measure(size); container.Arrange(new Rect(size)); container.UpdateLayout();
            }

            Arrange(1280, 840);
            var wideHeader = Bounds(header, container);
            var wideReconnect = Bounds(reconnect, container);
            var wideConfiguration = Bounds(configuration, container);
            var wideTabs = Bounds(tabs, container);
            Check(wideHeader.Height is >= 24 and <= 26 && ((Grid)container).RowDefinitions[0].Height.IsAuto,
                $"The app row uses natural 24-DIP compact chrome instead of retaining a fixed 44-DIP row; actual {wideHeader.Height}.");
            Check(wideTabs.Top <= 90, $"The workspace must start within 90 DIPs even with a disconnected banner; actual {wideTabs.Top}.");
            Check(navigation.ActualWidth >= 30 && panels.ActualWidth >= 30,
                "Navigation and plugin panels must remain directly reachable without duplicating the product banner.");
            Check(wideReconnect.Width > 0 && wideReconnect.Height is >= 24 and <= 26 &&
                wideConfiguration.Width > 0 && wideConfiguration.Height is >= 24 and <= 26 &&
                configuration.VerticalAlignment == VerticalAlignment.Center && reconnect.VerticalAlignment == VerticalAlignment.Center,
                "Reconnect and Configuration retain compact usable controls without stretching into the app row.");
            Check(wideConfiguration.Right <= container.ActualWidth - 10,
                "Configuration access must remain inside the compact header's right edge.");
            Check(ownership.Visibility == Visibility.Visible && ownership.ActualWidth > 0 && connectionStatus.ActualHeight > 0 && connectionBanner?.Visibility == Visibility.Visible,
                "Disconnected status must remain available through its compact indicator and actionable banner.");
            Check(AutomationProperties.GetName(configuration) == "Open Pegasus Cerebral Configuration",
                "The compact Configuration button must retain its complete accessible name.");

            Arrange(720, 516);
            var smallHeader = Bounds(header, container);
            var smallReconnect = Bounds(reconnect, container);
            var smallConfiguration = Bounds(configuration, container);
            var smallTabs = Bounds(tabs, container);
            var smallSystemStatus = Bounds(systemStatus, container);
            var smallSystemStatusMetrics = Bounds(systemStatusMetrics, container);
            var smallReadouts = Bounds(systemReadouts, container);
            var smallStatusDetails = Bounds(statusDetails, container);
            Check(smallHeader.Height is >= 24 and <= 26,
                $"The app row stays compact at a constrained high-DPI-sized work area; actual {smallHeader.Height}.");
            Check(smallTabs.Top <= 90 && smallTabs.Height >= 400,
                $"A short work area must reserve useful tab space; tabs are top={smallTabs.Top}, height={smallTabs.Height}.");
            Check(smallSystemStatus.Height >= 29 && smallSystemStatus.Top >= smallTabs.Bottom - 1,
                "The compact system metrics bar must remain visible below the usable tab surface.");
            Check(systemReadouts.ClipToBounds && smallReadouts.Width > 0 &&
                   smallReadouts.Right <= smallStatusDetails.Left + 1 && smallStatusDetails.Right <= smallSystemStatus.Right &&
                   VisualTreeHelper.GetParent(systemStatusMetrics) is Grid { ClipToBounds: true } metricClip &&
                   Bounds(metricClip, container).Left >= smallReadouts.Left &&
                   Bounds(metricClip, container).Right <= smallReadouts.Right + 1,
                "Visible resource/slot/activity readouts retain a clipped region beside reachable Status details at narrow width.");
            var chat = Bounds(pageButtons["chat"], container);
            Check(chat.Left >= 0 && Bounds(pageButtons["files"], container).Right <= Bounds(ownership, container).Left &&
                  !pageButtons.ContainsKey("work"),
                "Primary Chat and Files navigation must not overlap the right-side actions, while workflow details stay out of the main tab row.");
            Check(smallReconnect.Left >= 0 && smallConfiguration.Right <= container.ActualWidth - 10 && smallConfiguration.Bottom <= smallTabs.Top,
                "Connection actions must remain inside the short layout above the tabs.");
            var pluginSettings = (Button)Find(container, "ReviewDesktopPlugins");
            RefreshConnectionBannerActions(true, true);
            Check(reconnect.Visibility == Visibility.Collapsed && pluginSettings.Visibility == Visibility.Visible &&
                  Equals(pluginSettings.Content, "Open Configuration"),
                "A connected plugin load failure offers plugin settings, not a worker reconnect.");
            RefreshConnectionBannerActions(true, false);
            Check(reconnect.Visibility == Visibility.Collapsed && pluginSettings.Visibility == Visibility.Collapsed,
                "Other connected errors must not suggest a reconnect or unrelated plugin settings.");
            RefreshConnectionBannerActions(false, true);
            Check(reconnect.Visibility == Visibility.Visible && pluginSettings.Visibility == Visibility.Collapsed,
                "An unavailable worker connection retains the reconnect action even with an earlier plugin warning.");
            RefreshConnectionBannerActions(false, false);
            Check(reconnect.Visibility == Visibility.Visible && pluginSettings.Visibility == Visibility.Collapsed,
                "An ordinary offline banner retains its reconnect action.");
            RefreshShellConnection();
            var originalDraft = prompt.Text; prompt.Text = "Keep this draft across named surfaces.";
            SelectPage("files"); Check(ReferenceEquals(tabs.SelectedItem, pages["files"]), "Files selects its named surface.");
            SelectPage("work"); Check(ReferenceEquals(tabs.SelectedItem, pages["work"]) && pages["work"].Content is WorkflowWorkspace,
                "The internal workflow-details surface remains callable without a primary Work tab.");
            SelectPage("chat"); Check(ReferenceEquals(tabs.SelectedItem, pages["chat"]) && prompt.Text == "Keep this draft across named surfaces.",
                "Returning to Chat preserves the composer and existing conversation control identity.");
            prompt.Text = originalDraft; Arrange(720, 516);

            var scaledRender = new RenderTargetBitmap(1080, 774, 144, 144, PixelFormats.Pbgra32);
            var scaledBackdrop = new DrawingVisual();
            using (var drawing = scaledBackdrop.RenderOpen()) drawing.DrawRectangle(Background, null, new Rect(0, 0, 720, 516));
            scaledRender.Render(scaledBackdrop); scaledRender.Render(container);
            Check(scaledRender.PixelWidth == 1080 && scaledRender.PixelHeight == 774,
                "The 720x516-DIP layout must render to the expected 1080x774 pixels at 150% DPI.");
            var doubleScale = new RenderTargetBitmap(1440, 1032, 192, 192, PixelFormats.Pbgra32); doubleScale.Render(container);
            Check(doubleScale.PixelWidth == 1440 && doubleScale.PixelHeight == 1032,
                "The same bounded native shell renders at 200% DPI without a second pixel-specific layout.");

            string? scaledSmallPreviewPath = null;
            if (previewPath is not null)
            {
                if (!Path.IsPathFullyQualified(previewPath) || !previewPath.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
                    throw new ArgumentException("Preview needs an absolute PNG path.");
                Directory.CreateDirectory(Path.GetDirectoryName(previewPath)!);
                scaledSmallPreviewPath = Path.Combine(Path.GetDirectoryName(previewPath)!, Path.GetFileNameWithoutExtension(previewPath) + ".small-150.png");
                var scaledEncoder = new PngBitmapEncoder(); scaledEncoder.Frames.Add(BitmapFrame.Create(scaledRender));
                using (var scaledOutput = new FileStream(scaledSmallPreviewPath, FileMode.Create, FileAccess.Write)) scaledEncoder.Save(scaledOutput);
                Arrange(1280, 840);
                var preview = new RenderTargetBitmap(1280, 840, 96, 96, PixelFormats.Pbgra32);
                var backdrop = new DrawingVisual();
                using (var drawing = backdrop.RenderOpen()) drawing.DrawRectangle(Background, null, new Rect(0, 0, 1280, 840));
                preview.Render(backdrop); preview.Render(container);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(preview));
                using var output = new FileStream(previewPath, FileMode.Create, FileAccess.Write); encoder.Save(output);
            }

            Console.WriteLine(JsonSerializer.Serialize(new
            {
                success = true,
                headerChecks = checks,
                wide = new { width = Rounded(wideHeader.Width), headerHeight = Rounded(wideHeader.Height), tabsTop = Rounded(wideTabs.Top), tabsHeight = Rounded(wideTabs.Height) },
                scaledSmall = new { width = Rounded(smallHeader.Width), headerHeight = Rounded(smallHeader.Height), tabsTop = Rounded(smallTabs.Top), tabsHeight = Rounded(smallTabs.Height), dpi = 144, pixels = "1080x774" },
                configurationLaunched = false,
                serviceCommandsExecuted = false,
                windowShown = false,
                previewPath,
                scaledSmallPreviewPath
            }));
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(JsonSerializer.Serialize(new { success = false, headerChecks = checks, error = ex.ToString() }));
            return 1;
        }
        finally { Close(); }
    }
    private int CheckScopedAttentionBannerUi()
    {
        if (IsConnected) throw new InvalidOperationException("Attention checks require the owned offline diagnostic window.");
        var checks = 0; var commands = 0;
        void Check(bool value, string label) { if (!value) throw new InvalidOperationException(label); checks++; }
        static T Find<T>(DependencyObject root, string id) where T : FrameworkElement
        {
            if (root is T item && AutomationProperties.GetAutomationId(item) == id) return item;
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            {
                try { return Find<T>(VisualTreeHelper.GetChild(root, i), id); } catch (KeyNotFoundException) { }
            }
            throw new KeyNotFoundException(id);
        }
        static Rect Bounds(FrameworkElement item, FrameworkElement root) =>
            new(item.TransformToAncestor(root).Transform(new Point()), new Size(item.ActualWidth, item.ActualHeight));
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var connectedField = typeof(ConnectedWindow).GetField("<IsConnected>k__BackingField", flags)!;
        var clientField = typeof(ConnectedWindow).GetField("client", flags)!;
        var oldClient = clientField.GetValue(this); var oldChat = durableConversationId;
        var oldNative = nativeChat; var oldView = nativeAttentionView; var oldHostContent = nativeAttentionHost!.Content;
        var oldOwner = mainCompletionOwner; var oldAttentionOwner = nativeAttentionOwner;
        var oldAttentionSnapshot = nativeAttentionSnapshot; var oldAttentionVersion = nativeAttentionConnectionVersion;
        var oldPublication = attentionPublication; var oldDismissed = dismissedAttention; var oldDisplayed = displayedAttention;
        var oldStatus = Status.Text; var oldBrush = Status.Foreground;
        var oldMirror = mainCompletionMirror; var oldMirrorBrush = mainCompletionMirrorBrush;
        using var handler = new AgentSidebarMetricsHandler((_, _) =>
        { commands++; throw new InvalidOperationException("No command is admitted by the banner layout fixture."); });
        using var fixtureClient = new CerebralClient();
        var http = typeof(CerebralClient).GetField("http", flags)!;
        ((System.Net.Http.HttpClient)http.GetValue(fixtureClient)!).Dispose();
        http.SetValue(fixtureClient, new System.Net.Http.HttpClient(handler, disposeHandler: false)
            { BaseAddress = new Uri("http://attention-fixture.invalid/"), Timeout = Timeout.InfiniteTimeSpan });
        using var fixture = new WorkflowWorkspace((_, _) =>
        { commands++; throw new InvalidOperationException("Banner layout does not submit or refresh work."); },
            () => Array.Empty<AgentProfile>(), (_, _) => { }, embedded: true);
        try
        {
            clientField.SetValue(this, fixtureClient); connectedField.SetValue(this, true);
            durableConversationId = "owned-attention-chat"; nativeChat = fixture;
            fixture.BeginConversation(durableConversationId);
            nativeAttentionView = fixture.CreateHostAttentionView(NativeAttentionCurrent);
            nativeAttentionHost.Content = nativeAttentionView;
            mainCompletionOwner = new(fixtureClient, ServiceConnection, knowledgeConnectionVersion, ServiceConnectionIdentity);
            CaptureNativeAttentionOwner();
            var diagnostic = string.Join("\n", Enumerable.Range(1, 64).Select(i => $"Controlled diagnostic line {i}: full details remain readable."));
            var nativeScroll = Find<ScrollViewer>(nativeAttentionView, "WorkflowAttentionText");
            var nativeState = (TextBlock)nativeScroll.Content;
            nativeState.Text = diagnostic; nativeState.Foreground = Ui.ErrorText;
            nativeAttentionView.Visibility = Visibility.Visible;
            SetStatus(diagnostic, true); RefreshShellConnection();
            var originalSnapshot = fixture.Snapshot;
            var root = (FrameworkElement)Content;
            root.Measure(new Size(720, 516)); root.Arrange(new Rect(0, 0, 720, 516)); root.UpdateLayout();
            var genericScroll = Find<ScrollViewer>(root, "ConnectionAttentionText");
            var refresh = Find<Button>(nativeAttentionView, "WorkflowRefresh");
            var bannerBounds = Bounds(connectionBanner!, root);
            var tabBounds = Bounds(Tabs, root);
            Check(nativeScroll.ScrollableHeight > 0 && genericScroll.ScrollableHeight > 0 &&
                nativeScroll.ActualHeight <= 72.5 && genericScroll.ActualHeight <= 72.5 &&
                nativeState.Text == diagnostic && Status.Text == diagnostic,
                "Both complete multiline diagnostics remain in bounded readable scroll regions without text truncation.");
            Check(bannerBounds.Height <= 230 && tabBounds.Height >= 200 &&
                refresh.ActualHeight is >= 24 and <= 26 && dismissAttentionButton!.ActualHeight is >= 24 and <= 26 &&
                !refresh.IsDescendantOf(nativeScroll) && !dismissAttentionButton.IsDescendantOf(genericScroll) &&
                Bounds(refresh, root).Bottom <= bannerBounds.Bottom + 1 &&
                Bounds(dismissAttentionButton, root).Bottom <= bannerBounds.Bottom + 1,
                "A short window retains useful Main space and independently reachable attention actions outside both text scrollers.");
            dismissAttentionButton!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(connectionBanner!.Visibility == Visibility.Collapsed && Status.Text == diagnostic &&
                ReferenceEquals(fixture.Snapshot, originalSnapshot) && mainCompletionMirror == oldMirror &&
                ReferenceEquals(mainCompletionMirrorBrush, oldMirrorBrush),
                "The real guarded Dismiss callback hides only the connected current notice, preserving request and completion state.");
            connectedField.SetValue(this, false); RefreshShellConnection();
            Check(connectionBanner.Visibility == Visibility.Visible && reconnectServiceButton!.Visibility == Visibility.Visible &&
                Status.Text == diagnostic && ReferenceEquals(fixture.Snapshot, originalSnapshot),
                "Offline connection controls remain visible even when the exact same notice was dismissed while connected.");
            connectedField.SetValue(this, true); SetStatus(diagnostic, true); RefreshShellConnection();
            Check(connectionBanner.Visibility == Visibility.Visible, "A fresh identical diagnostic publication is not suppressed.");
            dismissAttentionButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(connectionBanner.Visibility == Visibility.Collapsed, "The new connected publication can be dismissed through its own guarded button.");
            durableConversationId = "another-owned-attention-chat"; RefreshShellConnection();
            Check(connectionBanner.Visibility == Visibility.Visible && Status.Text == diagnostic &&
                ReferenceEquals(fixture.Snapshot, originalSnapshot) && commands == 0,
                "Changing chat scope restores the notice without replay, network, status reset or durable mutation.");
        }
        finally
        {
            nativeChat = oldNative; nativeAttentionView = oldView; nativeAttentionHost.Content = oldHostContent;
            clientField.SetValue(this, oldClient); connectedField.SetValue(this, false); durableConversationId = oldChat;
            mainCompletionOwner = oldOwner; nativeAttentionOwner = oldAttentionOwner; nativeAttentionSnapshot = oldAttentionSnapshot;
            nativeAttentionConnectionVersion = oldAttentionVersion; attentionPublication = oldPublication;
            dismissedAttention = oldDismissed; displayedAttention = oldDisplayed;
            Status.Text = oldStatus; Status.Foreground = oldBrush; RefreshShellConnection();
        }
        return checks;
    }

}
