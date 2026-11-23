using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Cerebral.Desktop;

namespace Cerebral.Orchestrator;

public sealed partial class OrchestratorWindow
{
    private Popup? composerToolsPopup;
    private Button? composerToolsButton;
    private Border? composerToolsFrame;
    private ScrollViewer? composerToolsScroll;
    private FrameworkElement? composerTextEntryTools;
    private ContentControl? composerDictationTools;
    private Button? composerDictationButton;
    private Popup? composerDictationPopup;
    private ScrollViewer? composerDictationScroll;

    private Grid? composerLayerHost;
    private GridSplitter? composerVerticalDivider;
    private double? composerPreferredHeight;
    private double? composerAppliedHeight;
    private bool updatingComposerRegion;

    private bool ComposerQuestionIsActive =>
        nativeChat?.Snapshot?.Run?.Questions is not null && IsNativeChat ||
        questionHost?.Visibility == Visibility.Visible;

    private GridSplitter CreateComposerVerticalDivider()
    {
        var divider = new GridSplitter
        {
            Height = 5, HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Center, ResizeDirection = GridResizeDirection.Rows,
            ResizeBehavior = GridResizeBehavior.PreviousAndNext, ShowsPreview = true, Background = Ui.Line,
            ToolTip = "Drag to resize the message box; arrow keys resize when focused."
        };
        AutomationProperties.SetAutomationId(divider, "ComposerVerticalDivider");
        AutomationProperties.SetName(divider, "Resize message box vertically");
        divider.DragStarted += (_, _) =>
            SetComposerPreferredHeight(conversationCenter?.RowDefinitions[4].ActualHeight ?? 0);
        divider.DragCompleted += (_, _) =>
            SetComposerPreferredHeight(conversationCenter?.RowDefinitions[4].ActualHeight ?? 0);
        divider.KeyUp += (_, args) =>
        {
            if (args.Key is System.Windows.Input.Key.Up or System.Windows.Input.Key.Down)
                SetComposerPreferredHeight(conversationCenter?.RowDefinitions[4].ActualHeight ?? 0);
        };
        return divider;
    }

    private void SetComposerPreferredHeight(double height)
    {
        if (!double.IsFinite(height) || height <= 0) return;
        composerPreferredHeight = height;
        UpdateDesktopInputGuidanceLayout();
        MainLayoutChanged();
    }

    // Generic guidance owns this region while it is visible. Interception supplies/clears its
    // contents; this method changes presentation only and is also safe during viewport resize.
    private void UpdateDesktopInputGuidanceLayout()
    {
        if (updatingComposerRegion || conversationCenter is null || composerLayerHost is null ||
            conversationComposer is null || conversationCenter.RowDefinitions.Count < 5) return;
        updatingComposerRegion = true;
        try
        {
            var question = ComposerQuestionIsActive;
            var guidance = desktopInputGuidanceHost.Visibility == Visibility.Visible && !question;
            var row = conversationCenter.RowDefinitions[4];
            var transcriptRow = conversationCenter.RowDefinitions[2];
            // Grid can report an oversized ActualHeight when old pixel rows plus minimums
            // outgrow a newly smaller parent. Clamp against the parent's allocated layout slot.
            var slotHeight = LayoutInformation.GetLayoutSlot(conversationCenter).Height;
            var middleHeight = slotHeight > 0 ? slotHeight : conversationCenter.ActualHeight;
            var available = Math.Max(0, middleHeight -
                conversationCenter.RowDefinitions[0].ActualHeight - conversationCenter.RowDefinitions[1].ActualHeight -
                (conversationCenter.RowDefinitions.Count > 5 ? conversationCenter.RowDefinitions[5].ActualHeight : 0) - 5);
            var chrome = conversationComposer.Children.OfType<FrameworkElement>()
                .Where(child => !ReferenceEquals(child, prompt) && !ReferenceEquals(child, composerSuggestionSurface) && child.Visibility != Visibility.Collapsed)
                .Sum(child => child.DesiredSize.Height) + conversationComposer.Margin.Top + conversationComposer.Margin.Bottom;
            // Reserve readable transcript space whenever the actual composer minimum can fit.
            // Default text growth must obey the same viewport budget as a dragged divider.
            // On compact windows, keep a short transcript beside the review without taking
            // more than half of the available space to reach the 90-DIP floor.
            var reviewTranscriptMinimum = Math.Min(180, Math.Max(available * .3, Math.Min(90, available / 2)));
            var transcriptMinimum = guidance || question ? reviewTranscriptMinimum :
                Math.Min(180, Math.Max(0, available - chrome - prompt.MinHeight));
            if (!guidance && !question && row.Height.IsAbsolute && composerAppliedHeight is { } applied &&
                Math.Abs(row.Height.Value - applied) > .5)
                composerPreferredHeight = row.Height.Value;
            transcriptRow.MinHeight = transcriptMinimum;
            var regionMinimum = 0d;
            var regionMaximum = double.PositiveInfinity;
            var regionHeight = GridLength.Auto;
            composerLayerHost.Visibility = question ? Visibility.Collapsed : Visibility.Visible;
            if (composerVerticalDivider is not null)
                composerVerticalDivider.Visibility = guidance || question ? Visibility.Collapsed : Visibility.Visible;
            desktopInputGuidanceHost.VerticalAlignment = VerticalAlignment.Stretch;
            desktopInputGuidanceHost.HorizontalAlignment = HorizontalAlignment.Stretch;
            if (guidance)
            {
                regionHeight = new GridLength(2, GridUnitType.Star);
                if (desktopInputGuidanceHost.Child is FrameworkElement child)
                {
                    child.MaxHeight = double.PositiveInfinity;
                    child.VerticalAlignment = VerticalAlignment.Stretch;
                }
            }
            else if (!question && composerPreferredHeight is { } preferred && available > 0)
            {
                var minimum = chrome + prompt.MinHeight;
                var maximum = Math.Max(minimum, available - transcriptMinimum);
                var height = Math.Clamp(preferred, minimum, maximum);
                regionMinimum = minimum; regionMaximum = maximum; regionHeight = new GridLength(height);
                composerAppliedHeight = height;
                prompt.MaxHeight = double.PositiveInfinity;
                prompt.Height = Math.Max(prompt.MinHeight, height - chrome);
            }
            else
            {
                prompt.Height = double.NaN;
                if (!guidance && !question && available > 0)
                {
                    var compact = conversationLayout?.ActualHeight is > 0 and < 560;
                    prompt.MaxHeight = Math.Max(prompt.MinHeight, Math.Min(compact ? 96 : 160, available - transcriptMinimum - chrome));
                }
            }
            // Assign final constraints only. Repeated layout observation must not temporarily reset
            // bounds and invalidate layout again when wrapped controls have already settled.
            row.MaxHeight = regionMaximum; row.MinHeight = regionMinimum; row.Height = regionHeight;
            conversationComposer.Visibility = question || guidance ? Visibility.Collapsed : Visibility.Visible;
            if (question || guidance)
            {
                if (composerOptionsPopup is not null) composerOptionsPopup.IsOpen = false;
                if (composerToolsPopup is not null) composerToolsPopup.IsOpen = false;
                if (composerDictationPopup is not null) composerDictationPopup.IsOpen = false;
            }
        }
        finally { updatingComposerRegion = false; }
    }

    private Button BuildComposerPluginTools()
    {
        var modes = DesktopInputModeView();
        // Input/refinement choices belong in Tools; retain their actual plugin controls.
        if (!desktopInputModeBar.Children.Contains(desktopInputQuickToggles))
            desktopInputModeBar.Children.Add(desktopInputQuickToggles);
        desktopInputQuickToggles.Margin = new Thickness(3, 0, 5, 0);
        composerTextEntryTools = RegisterDesktopTextEntry(prompt, "composer", "Message to Cerebral");
        var content = new StackPanel { VerticalAlignment = VerticalAlignment.Top };
        var heading = Ui.Text("Tools & input", 16); heading.FontWeight = FontWeights.SemiBold;
        heading.Margin = new Thickness(0, 0, 0, 8); content.Children.Add(heading);
        var explanation = Ui.Text("Tools from enabled plugins use this message and its current context.", 12, Ui.Muted);
        explanation.Margin = new Thickness(0, 0, 0, 8); content.Children.Add(explanation);
        content.Children.Add(modes);
        content.Children.Add(desktopPluginActions);
        content.Children.Add(desktopPluginAccessories);
        composerDictationTools = new ContentControl
        {
            HorizontalContentAlignment = HorizontalAlignment.Stretch
        };
        MountVisibleComposerInputTools();
        var dictationContent = new StackPanel();
        dictationContent.Children.Add(Ui.Text("Dictation", 16));
        dictationContent.Children.Add(composerDictationTools);
        composerDictationScroll = new ScrollViewer { Content = dictationContent,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            MaxHeight = 240 };
        composerDictationButton = ShellButton("🎙", "ComposerDictation", "Dictation controls", () =>
        {
            if (composerDictationPopup is null) return;
            if (composerToolsPopup is not null) composerToolsPopup.IsOpen = false;
            if (composerOptionsPopup is not null) composerOptionsPopup.IsOpen = false;
            composerDictationPopup.IsOpen = !composerDictationPopup.IsOpen;
        });
        composerDictationButton.Margin = new Thickness(8, 0, 0, 0);
        var dictationFrame = new Border { Child = composerDictationScroll, Padding = new Thickness(12),
            Background = Ui.Surface, BorderBrush = Ui.Line, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6), MinWidth = 250, MaxWidth = 440 };
        AutomationProperties.SetAutomationId(dictationFrame, "ComposerDictationControls");
        composerDictationPopup = new Popup { PlacementTarget = composerDictationButton, Placement = PlacementMode.Top,
            StaysOpen = false, AllowsTransparency = true, Child = dictationFrame };
        dictationFrame.PreviewKeyDown += (_, args) =>
        { if (args.Key == System.Windows.Input.Key.Escape) { composerDictationPopup.IsOpen = false; args.Handled = true; } };
        composerToolsScroll = new ScrollViewer
        {
            Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalContentAlignment = VerticalAlignment.Top,
            MaxHeight = 300, CanContentScroll = false
        };
        composerToolsFrame = new Border
        {
            Child = composerToolsScroll, Padding = new Thickness(12), Background = Ui.Surface,
            BorderBrush = Ui.Line, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Width = 420
        };
        AutomationProperties.SetAutomationId(composerToolsFrame, "ComposerPluginTools");
        AutomationProperties.SetName(composerToolsFrame, "Plugin actions and text-entry tools for the current message");
        composerToolsButton = ShellButton("Tools", "ComposerTools", "Plugin actions, dictation and other input tools", () =>
        {
            if (composerToolsPopup is null) return;
            if (composerOptionsPopup is not null) composerOptionsPopup.IsOpen = false;
            if (composerDictationPopup is not null) composerDictationPopup.IsOpen = false;
            composerToolsPopup.IsOpen = !composerToolsPopup.IsOpen;
        });
        composerToolsPopup = new Popup
        {
            PlacementTarget = composerToolsButton, Placement = PlacementMode.Top,
            StaysOpen = false, AllowsTransparency = true, Child = composerToolsFrame
        };
        Closed += (_, _) =>
        { if (composerToolsPopup is not null) composerToolsPopup.IsOpen = false; if (composerDictationPopup is not null) composerDictationPopup.IsOpen = false; };
        return composerToolsButton;
    }

    private void UpdateComposerPluginTools(double width)
    {
        // The secondary surface grows only within the available composer, then scrolls.
        // No plugin action is dropped or recreated to fit a smaller window.
        desktopInputQuickToggles.MaxWidth = Math.Max(120, width - 110);
        if (composerToolsFrame is not null) composerToolsFrame.Width = Math.Clamp(width - 20, 260, 460);
        if (composerToolsScroll is not null)
            composerToolsScroll.MaxHeight = Math.Clamp((conversationLayout?.ActualHeight ?? 700) * .45, 140, 300);
        if (composerDictationScroll is not null) composerDictationScroll.MaxHeight = Math.Clamp((conversationLayout?.ActualHeight ?? 700) * .4, 120, 240);
    }
}
