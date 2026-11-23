using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Cerebral.Core;
using Cerebral.Desktop;

namespace Cerebral.Orchestrator;

public sealed partial class OrchestratorWindow
{
    // Offscreen WPF layout with task-owned fake controls. No service, plugin execution, visible popup or model is started.
    public int CheckComposerLayoutUi(string? previewDirectory = null)
    {
        var checks = 0;
        void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); checks++; }
        static FrameworkElement Find(DependencyObject parent, string id)
        {
            if (parent is FrameworkElement element && AutomationProperties.GetAutomationId(element) == id) return element;
            for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
            {
                var child = VisualTreeHelper.GetChild(parent, index);
                try { return Find(child, id); } catch (InvalidOperationException) { }
            }
            throw new InvalidOperationException("Missing composer control: " + id);
        }
        static Rect Bounds(FrameworkElement view, FrameworkElement ancestor) =>
            new(view.TransformToAncestor(ancestor).Transform(new Point()), new Size(view.ActualWidth, view.ActualHeight));
        static void Capture(FrameworkElement view, int width, int height, string destination)
        {
            var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            var background = new DrawingVisual();
            using (var drawing = background.RenderOpen()) drawing.DrawRectangle(Ui.Surface, null, new Rect(0, 0, width, height));
            bitmap.Render(background); bitmap.Render(view);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write); encoder.Save(output);
        }
        var priorTheme = Ui.AppearanceTheme;
        try
        {
            if (previewDirectory is not null && (!Path.IsPathFullyQualified(previewDirectory) || !Directory.Exists(previewDirectory)))
                throw new ArgumentException("Composer layout previews require an existing absolute task-owned directory.");
            var container = (FrameworkElement)Content;
            SelectPage("chat");
            checks += CheckManagedSlotPresentation();
            checks += CheckSystemResourceUi();
            systemMetricLine!.SetValues(["6% / 24%", "2 / 16 GiB", "28%", "2 / 8 GiB", "3 free / 6 total", "2"],
                "Controlled layout fixture values; no live resource observation.");
            systemSlotStatusText.Text = "Local: 3 free / 6 total · Queued 2";
            inferenceOwnershipStatusText.Text = "2 working agents · 3 contexts";
            systemWorkStateText.Text = "Work hold off · 1 local model loaded";
            checks += CheckChatRailAndWorkPanels(container, previewDirectory);
            container.Measure(new Size(1280, 840)); container.Arrange(new Rect(0, 0, 1280, 840)); container.UpdateLayout();
            var primary = (WrapPanel)Find(container, "ComposerPrimaryActions");
            var send = (Button)Find(container, "SendMessage");
            var applicationHeader = Find(container, "ApplicationHeader");
            Check(send.MinHeight == 24 && send.ActualHeight is >= 24 and <= 26 &&
                send.VerticalAlignment == VerticalAlignment.Center && send.Padding.Top <= 2 && send.Padding.Bottom <= 2 &&
                applicationHeader.ActualHeight is >= 24 and <= 26 &&
                ((Grid)container).RowDefinitions[0].Height.IsAuto,
                "Local Main actions and the natural application row use compact 24-DIP controls at normal font size.");

            Check(primary.Children[0] == send && !primary.Children.Contains(desktopInputQuickToggles),
                "Send stays first while prompt refinement belongs in Tools.");
            Check(desktopInputModeBar.Children.Contains(desktopInputQuickToggles) &&
                ReferenceEquals(desktopInputQuickToggles.Parent, desktopInputModeBar),
                "Tools retains the original input-mode and refinement controls.");
            Check(composerHeader is not null && ReferenceEquals(composerHeader, composerSubmit) &&
                composerCaption is { Visibility: Visibility.Collapsed, Parent: null } &&
                AutomationProperties.GetName(prompt) == "Message to Cerebral" &&
                ReferenceEquals(composerDictationButton!.Parent, composerSubmit) && Grid.GetColumn(composerDictationButton) == 2 &&
                ReferenceEquals(composerOptionsPanel!.Parent, composerSubmit) && Grid.GetColumn(composerOptionsPanel) == 1,
                "Response options and microphone share the action footer; the editor keeps its accessible label without an extra caption row.");
            Check(composerToolsPopup is { StaysOpen: false, IsOpen: false } && composerToolsButton is not null &&
                ReferenceEquals(composerToolsPopup.PlacementTarget, composerToolsButton) &&
                ReferenceEquals(composerToolsFrame?.Child, composerToolsScroll),
                "Secondary plugin tools use one dismissible overflow surface without opening a visible popup during checks.");
            Check(composerToolsScroll is { HorizontalScrollBarVisibility: ScrollBarVisibility.Disabled,
                VerticalScrollBarVisibility: ScrollBarVisibility.Auto, VerticalContentAlignment: VerticalAlignment.Top },
                "Tool overflow must scroll vertically inside its bounded top-aligned surface.");
            var toolContent = (StackPanel)composerToolsScroll!.Content;
            Check(toolContent.Children.Contains(desktopPluginActions) && toolContent.Children.Contains(desktopInputModeBar) &&
                toolContent.Children.Contains(desktopPluginAccessories) &&
                ReferenceEquals(composerDictationTools!.Content, composerTextEntryTools) && !toolContent.Children.Contains(composerTextEntryTools!) &&
                !primary.Children.Contains(desktopPluginActions),
                "Tools contains refinement and plugin accessories; microphone controls retain their own popup mount.");
            Check(composerDictationPopup is { StaysOpen: false, IsOpen: false } &&
                ReferenceEquals(composerDictationPopup.PlacementTarget, composerDictationButton),
                "Dictation uses a closed, dismissible popup without starting capture.");
            var dictationActions = (Panel)((Panel)composerTextEntryTools!).Children[0];
            var dictated = 0;
            var dictate = Ui.Button("Dictate", () => dictated++);
            var auto = new CheckBox { Content = "Auto" };
            dictationActions.Children.Add(dictate); dictationActions.Children.Add(auto);
            var refinement = new CheckBox { Content = "Refine my prompt", Margin = new Thickness(2, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
            AutomationProperties.SetAutomationId(refinement, "DesktopInputModeToggle.fixture");
            var toggled = 0; refinement.Checked += (_, _) => toggled++;
            desktopInputQuickToggles.Children.Add(refinement);
            var clicked = 0; var actions = new List<Button>();
            for (var index = 0; index < 40; index++)
            {
                var button = Ui.Button("Plugin tool " + index.ToString("D2"), () => clicked++);
                AutomationProperties.SetAutomationId(button, "FixtureComposerAction." + index);
                actions.Add(button); desktopPluginActions.Children.Add(button);
            }
            desktopPluginActions.Visibility = Visibility.Visible;
            prompt.Text = "A draft with café and a second line.\nKeep the draft while reviewing tools.";
            foreach (var theme in new[] { AppearanceThemePolicy.Light, AppearanceThemePolicy.Dark })
            {
                Ui.ApplyAppearanceTheme(theme);
                foreach (var viewport in new[] { new Size(1280, 840), new Size(1024, 768), new Size(720, 540) })
                {
                    container.Measure(viewport); container.Arrange(new Rect(viewport)); container.UpdateLayout();
                    var sendBounds = Bounds(send, container);
                    Check(sendBounds.Width >= 40 && sendBounds.Height >= 24 && sendBounds.Left >= 0 &&
                        sendBounds.Right <= container.ActualWidth && Bounds(composerToolsButton!, container).Right <= container.ActualWidth,
                        "Primary Send and Tools actions must remain inside each tested composer viewport.");
                    var microphoneBounds = Bounds(composerDictationButton!, container);
                    var footerBounds = Bounds(composerSubmit!, container);
                    Check(microphoneBounds.Right <= container.ActualWidth && microphoneBounds.Right >= footerBounds.Right - 1 &&
                        footerBounds.Top >= Bounds(prompt, container).Bottom && microphoneBounds.Bottom <= footerBounds.Bottom + .5 &&
                        Bounds(primary, container).Right <= microphoneBounds.Left + .5 && composerDictationPopup!.IsOpen == false &&
                        double.IsNaN(send.Height) && send.Padding.Top <= 2 && send.Padding.Bottom <= 2,
                        "Response options and microphone fit below the editor beside compact, auto-height actions without overlap.");
                    // At 720×540 the header and minimum-height editor may leave less than 180 DIPs.
                    // Apply the same readable floor used by the resized-composer check below.
                    var transcriptFloor = viewport.Width == 720 ? 90 : 180;
                    Check(transcript.ActualHeight >= transcriptFloor && conversationComposer!.ActualHeight < 250,
                        $"Plugin action count cannot expand the composer until it crowds out the conversation: viewport={viewport}, transcript={transcript.ActualHeight:F1}, composer={conversationComposer!.ActualHeight:F1}, prompt={prompt.ActualHeight:F1}, transcriptRowMinimum={conversationCenter!.RowDefinitions[2].MinHeight:F1}, panels={workPanelLayout!.ActualHeight:F1}, Main={mainWorkPanel!.ActualHeight:F1}, teams={teamWorkPanelGroup!.ActualHeight:F1}, plugins={pluginWorkPanelCards.ActualHeight:F1}, status={systemStatusBar!.ActualHeight:F1}.");
                    Check(!conversationComposer!.Children.Contains(composerTextEntryTools!) && !dictationActions.IsVisible,
                        "Populated Dictate and Auto controls cannot consume space in the message editor while closed.");
                    Check(desktopPluginActions.Children.Cast<UIElement>().SequenceEqual(actions) &&
                        actions.Select(AutomationProperties.GetAutomationId).Distinct(StringComparer.Ordinal).Count() == actions.Count &&
                        ReferenceEquals(composerDictationTools.Content, composerTextEntryTools) &&
                        ReferenceEquals(desktopInputModeSelection.Parent, desktopInputModeBar),
                        "Narrow/wide layout must preserve every control instance, semantic ID, action order and input-mode mount.");
                    composerToolsFrame!.Measure(new Size(composerToolsFrame.Width, double.PositiveInfinity));
                    composerToolsFrame.Arrange(new Rect(0, 0, composerToolsFrame.Width, composerToolsFrame.DesiredSize.Height));
                    composerToolsFrame.UpdateLayout();
                    Check(composerToolsFrame.ActualHeight <= 326 && composerToolsScroll.ScrollableHeight > 0 &&
                        composerToolsScroll.ExtentHeight > composerToolsScroll.ViewportHeight,
                        "Many plugin actions must overflow through the bounded tools viewport without being removed.");
                    composerToolsScroll.ScrollToEnd(); composerToolsFrame.UpdateLayout();
                    Check(composerToolsScroll.VerticalOffset > 0, "The last plugin actions must be reachable by scrolling.");
                    composerToolsScroll.ScrollToTop();
                    Check(!composerToolsPopup!.IsOpen && prompt.Text.Contains("café", StringComparison.Ordinal),
                        "Layout inspection must never show a popup or lose the current draft.");
                    if (previewDirectory is not null)
                    {
                        Capture(container, (int)viewport.Width, (int)viewport.Height,
                            Path.Combine(previewDirectory, "composer-" + theme + "-" + (int)viewport.Width + ".png"));
                        if (viewport.Width == 1024) Capture(composerToolsFrame, (int)Math.Ceiling(composerToolsFrame.ActualWidth),
                            (int)Math.Ceiling(composerToolsFrame.ActualHeight), Path.Combine(previewDirectory, "tools-" + theme + ".png"));
                    }
                }
            }
            var scaledControls = primary.Children.OfType<Control>()
                .Concat(new Control[] { composerOptionsButton!, composerDictationButton!, enterSends, conversationTokenBalanceExpander }).ToArray();
            var priorFonts = scaledControls.Select(control => control.FontSize).ToArray();
            try
            {
                foreach (var control in scaledControls) control.FontSize = Math.Max(18, control.FontSize);
                container.Measure(new Size(1024, 768)); container.Arrange(new Rect(0, 0, 1024, 768)); container.UpdateLayout();
                var footer = Bounds(composerSubmit!, container);
                var measuredControls = scaledControls.Where(control => control.Visibility == Visibility.Visible && control.IsDescendantOf(container)).ToArray();
                Check(measuredControls.Length >= primary.Children.OfType<Control>().Count(control => control.Visibility == Visibility.Visible) + 1 &&
                    footer.Top >= Bounds(prompt, container).Bottom &&
                    measuredControls.All(control => control.ActualHeight >= 24 && control.ActualHeight + .5 >= control.DesiredSize.Height) &&
                    Bounds(composerDictationButton!, container).Bottom <= footer.Bottom + .5,
                    "Larger composer fonts grow the compact footer instead of clipping labels or overlapping the editor.");
                if (previewDirectory is not null) Capture(container, 1024, 768, Path.Combine(previewDirectory, "composer-scaled-font.png"));
            }
            finally
            {
                for (var index = 0; index < scaledControls.Length; index++) scaledControls[index].FontSize = priorFonts[index];
                container.Measure(new Size(1280, 840)); container.Arrange(new Rect(0, 0, 1280, 840)); container.UpdateLayout();
            }
            actions[0].RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, actions[0]));
            refinement.IsChecked = true;
            dictate.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, dictate));
            Check(clicked == 1 && toggled == 1 && dictated == 1,
                "Moving and scrolling existing controls must keep their original action and checkbox callbacks.");
            Check(Grid.GetRow(interfaceRequests) == 1 && questionHost is { MaxHeight: double.PositiveInfinity } &&
                ((StackPanel)transcript.Content).Children.Contains(questionHost),
                "Existing review and question surfaces remain separate and prominent, without a bounded answer box.");
            questionHost!.Visibility = Visibility.Visible; UpdateComposerQuestionSurface();
            Check(conversationComposer!.Visibility == Visibility.Collapsed && !composerToolsPopup!.IsOpen,
                "A question requiring input takes priority over the composer and its secondary tools.");
            questionHost.Visibility = Visibility.Collapsed; UpdateComposerQuestionSurface();
            Check(conversationComposer.Visibility == Visibility.Visible, "The composer returns after questions are dismissed.");
            Check(composerLayerHost is { MaxWidth: double.PositiveInfinity } &&
                composerVerticalDivider is { ResizeDirection: GridResizeDirection.Rows, ShowsPreview: true } &&
                Grid.GetRow(composerLayerHost) == 4 && Grid.GetRow(composerVerticalDivider) == 3,
                "The full-width middle region must expose a vertical divider without replacing composer controls.");
            var normalDraft = prompt.Text;
            var wide = new Size(1280, 840);
            container.Measure(wide); container.Arrange(new Rect(wide)); container.UpdateLayout();
            Check(Math.Abs(conversationComposer!.ActualWidth - composerLayerHost!.ActualWidth) <= 1 &&
                Math.Abs(composerLayerHost.ActualWidth - conversationCenter!.ActualWidth) <= 1,
                "Composer width must fill the available middle column.");
            SetComposerPreferredHeight(290);
            container.UpdateLayout();
            Check(conversationComposer.ActualHeight >= 280 && prompt.ActualHeight > 150 &&
                transcript.ActualHeight >= 180,
                "Enlarging the message region must grow the existing text box while retaining transcript room.");
            conversationCenter!.RowDefinitions[4].Height = new GridLength(320);
            UpdateDesktopInputGuidanceLayout(); container.UpdateLayout();
            Check(Math.Abs(composerPreferredHeight!.Value - 320) <= 1 && prompt.ActualHeight > 180,
                "A divider or keyboard row-size change must be adopted rather than immediately reset.");
            var guidance = new ScrollViewer { Content = new TextBox { Text = "Fixture review replacement", AcceptsReturn = true },
                MaxHeight = 430, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            desktopInputGuidanceHost.Child = guidance; desktopInputGuidanceHost.Visibility = Visibility.Visible;
            UpdateDesktopInputGuidanceLayout(); container.UpdateLayout();
            Check(conversationComposer.Visibility == Visibility.Collapsed && composerVerticalDivider!.Visibility == Visibility.Collapsed &&
                ReferenceEquals(desktopInputGuidanceHost.Parent, composerLayerHost) && guidance.MaxHeight == double.PositiveInfinity &&
                conversationCenter.RowDefinitions[4].Height.IsStar && desktopInputGuidanceHost.ActualHeight > 350 &&
                transcript.ActualHeight >= 120 && !composerToolsPopup!.IsOpen && !composerOptionsPopup!.IsOpen,
                "Generic guidance must prominently use the available middle area while hiding normal composer/tool clutter.");
            if (previewDirectory is not null)
                Capture(container, (int)wide.Width, (int)wide.Height, Path.Combine(previewDirectory, "review-prominent.png"));
            desktopInputGuidanceHost.Child = null; desktopInputGuidanceHost.Visibility = Visibility.Collapsed;
            UpdateDesktopInputGuidanceLayout(); container.UpdateLayout();
            Check(conversationComposer.Visibility == Visibility.Visible && composerVerticalDivider!.Visibility == Visibility.Visible &&
                prompt.Text == normalDraft && conversationCenter.RowDefinitions[4].Height.IsAbsolute &&
                Math.Abs(conversationCenter.RowDefinitions[4].Height.Value - 320) <= 1,
                "Closing guidance must restore the same draft and user-sized composer without sending.");
            container.Measure(new Size(720, 540)); container.Arrange(new Rect(0, 0, 720, 540)); container.UpdateLayout();
            Check(prompt.ActualHeight >= prompt.MinHeight && transcript.ActualHeight >= 90 &&
                Bounds(send, container).Bottom <= container.ActualHeight &&
                Math.Abs(prompt.ActualWidth - conversationCenter.ActualWidth) <= 1,
                $"A user-enlarged composer must fit a compact viewport, keep its actions reachable and retain transcript space: prompt={prompt.ActualHeight:F1}/{prompt.MinHeight:F1}, transcript={transcript.ActualHeight:F1}, sendBottom={Bounds(send, container).Bottom:F1}/{container.ActualHeight:F1}, widths={prompt.ActualWidth:F1}/{conversationCenter.ActualWidth:F1}, region={conversationCenter.RowDefinitions[4].ActualHeight:F1}, preferred={composerPreferredHeight:F1}, center={conversationCenter.ActualHeight:F1}, layout={conversationLayout!.ActualHeight:F1}, tabs={Tabs.ActualHeight:F1}, slots={LayoutInformation.GetLayoutSlot(conversationCenter).Height:F1}/{LayoutInformation.GetLayoutSlot(conversationLayout).Height:F1}, chrome={string.Join(";", conversationComposer.Children.OfType<FrameworkElement>().Where(x => !ReferenceEquals(x, prompt)).Select(x => x.GetType().Name + ":" + x.ActualHeight.ToString("F1") + "/" + x.DesiredSize.Height.ToString("F1")))}.");
            desktopInputGuidanceHost.Child = guidance; desktopInputGuidanceHost.Visibility = Visibility.Visible;
            UpdateDesktopInputGuidanceLayout(); container.UpdateLayout();
            Check(conversationComposer.Visibility == Visibility.Collapsed && desktopInputGuidanceHost.ActualHeight >= 150 &&
                transcript.ActualHeight >= 90 && composerLayerHost.Visibility == Visibility.Visible,
                $"Compact review must retain both the review editor and transcript: review={desktopInputGuidanceHost.ActualHeight:F1}, transcript={transcript.ActualHeight:F1}.");
            desktopInputGuidanceHost.Child = null; desktopInputGuidanceHost.Visibility = Visibility.Collapsed;
            UpdateDesktopInputGuidanceLayout();
            questionHost.Visibility = Visibility.Visible; UpdateComposerQuestionSurface(); container.UpdateLayout();
            Check(composerLayerHost.Visibility == Visibility.Collapsed && composerVerticalDivider!.Visibility == Visibility.Collapsed &&
                conversationComposer.Visibility == Visibility.Collapsed,
                "Questions must hide the complete normal message region rather than only its inner controls.");
            questionHost.Visibility = Visibility.Collapsed; UpdateComposerQuestionSurface(); container.UpdateLayout();
            Check(composerLayerHost.Visibility == Visibility.Visible && conversationComposer.Visibility == Visibility.Visible &&
                composerVerticalDivider!.Visibility == Visibility.Visible && prompt.Text == normalDraft,
                "Completing questions must restore the resized composer and divider immediately.");
            Console.WriteLine(JsonSerializer.Serialize(new { success = true, composerLayoutChecks = checks,
                evidence = "Offscreen deterministic native WPF layout and fake callback checks; 40 plugin controls, three viewport sizes, light/dark",
                visibleWindowOrPopupShown = false, serviceCommandsExecuted = false, pluginOrModelExecuted = false,
                ownerDataWritten = false, previewDirectory }));
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(JsonSerializer.Serialize(new { success = false, composerLayoutChecks = checks, error = error.ToString() }));
            return 1;
        }
        finally { Ui.ApplyAppearanceTheme(priorTheme); Close(); }
    }
}
