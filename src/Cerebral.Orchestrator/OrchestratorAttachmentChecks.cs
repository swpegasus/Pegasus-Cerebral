using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Cerebral.Client;
using Cerebral.Contracts;
using Cerebral.Contracts.Resources;
using Cerebral.Core;
using Cerebral.Desktop;
using Cerebral.Desktop.Plugins;

namespace Cerebral.Orchestrator;

/// <summary>Pure selection/host fixtures plus the real reusable native view; never constructs a ConnectedWindow.</summary>
public static class AttachmentComposerChecks
{
    public static async Task<int> VerifyAsync(string? previewPath = null)
    {
        var checks = 0;
        void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); checks++; }
        void Reject(Action action)
        {
            try { action(); } catch (InvalidOperationException) { checks++; return; }
            throw new InvalidOperationException("An invalid attachment selection was accepted.");
        }
        var binding = new DesktopConversationBinding("fixture-generation", "attachment-chat");
        var metadata = new Dictionary<string, string> { ["note"] = "Original metadata" };
        var reference = new AttachmentReference(Guid.NewGuid().ToString("N"), 1, "image/png", "diagram.png", 12500, metadata);
        AttachmentTransferSnapshot Upload(string id, AttachmentReference value) => new(
            new(id, "lead", binding.SessionId!, value.OriginalName, value.Length),
            new(id, "complete", null, value.Length, value.Length, value), false, false, null);
        var image = Upload("image-import", reference);
        var selection = AttachmentDraftSelection.Freeze(binding, "lead", [image]);
        metadata["note"] = "Changed outside snapshot";
        Check(selection.Content is [ImageContentPart] && ((ImageContentPart)selection.Content[0]).Attachment.Metadata!["note"] == "Original metadata",
            "Frozen native image selection deep-copies mutable metadata.");
        Reject(() => AttachmentDraftSelection.Freeze(binding, "another-profile", [image]));
        Reject(() => AttachmentDraftSelection.Freeze(binding with { SessionId = "another-chat" }, "lead", [image]));
        Reject(() => AttachmentDraftSelection.Freeze(binding, "lead", [image with { Uncertain = true }]));
        Reject(() => AttachmentDraftSelection.Freeze(binding, "lead", [image with { Busy = true }]));
        Reject(() => AttachmentDraftSelection.Freeze(binding, "lead", [image with { Status = null }]));
        var input = new DesktopSubmission(binding, "attachment-only", "");
        var request = WorkflowConversationClient.FreezeSubmission(new WorkflowConversationSubmission(input.RequestId, "lead",
            ConversationControlClient.ChatDefinitionId, ConversationControlClient.ChatDefinitionVersion, input.Text,
            CerebralJson.Element(new { responseSharePercent = 26 }), "Private interpretation")
            { AcceptedContent = selection.Content });
        Check(request.UserText == "" && request.AssignmentText == "Private interpretation" && request.AcceptedContent is [ImageContentPart] &&
            request.Input.GetProperty("responseSharePercent").GetInt32() == 26, "Attachment-only submission keeps visible text, interpretation and typed references separate.");
        var json = CerebralJson.Element(request).GetRawText();
        Check(json.Contains("diagram.png", StringComparison.Ordinal) && !json.Contains("storageKey", StringComparison.OrdinalIgnoreCase) &&
            !json.Contains("base64", StringComparison.OrdinalIgnoreCase), "Accepted content has readable metadata and no public path or binary payload.");

        var submitted = 0; var steered = 0;
        var frozen = new Dictionary<string, AttachmentDraftSelection> { [input.RequestId] = selection };
        bool HasContent(DesktopSubmission value) => frozen.TryGetValue(value.RequestId, out var saved) && saved.Binding == value.Binding && saved.Content.Count > 0;
        var dispatcher = Dispatcher.CurrentDispatcher;
        var host = new DesktopPluginHostAdapter(dispatcher, () => new(binding, "Fixture", null, null, false), () => new(binding, 0, ""),
            (_, _) => Task.FromResult(new DesktopOperationResult(DesktopOperationState.Completed)),
            (_, _) => { submitted++; return Task.FromResult(new DesktopOperationResult(DesktopOperationState.Accepted)); },
            (_, _) => { steered++; return Task.FromResult(new DesktopOperationResult(DesktopOperationState.Accepted)); },
            (_, _) => Task.FromResult(new DesktopOperationResult(DesktopOperationState.Accepted)), _ => { },
            hasSubmissionContent: HasContent);
        Check((await host.SubmitAsync(input)).State == DesktopOperationState.Accepted && submitted == 1,
            "Plugin handoff accepts empty text only when the exact request has a host-frozen selection.");
        Check((await host.SubmitAsync(input with { RequestId = "unfrozen" })).State == DesktopOperationState.Rejected &&
            (await host.SteerAsync(input)).State == DesktopOperationState.Rejected && steered == 0,
            "Unfrozen empty submissions and empty steering never dispatch.");
        Check((await host.SubmitAsync(input with { Binding = binding with { Generation = "changed" } })).State == DesktopOperationState.Rejected,
            "Stale plugin generation cannot reuse a selection.");

        var view = new AttachmentDraftView(); var openedIds = new List<string>(); var retryIds = new List<string>(); var removedIds = new List<string>();
        view.Present([
            new("image-import", "diagram.png", 12500, "Ready", null, true, false, true),
            new("text-import", "notes.txt", 812, "Unconfirmed", "Reply lost; retry the same import.", true, true, true)
        ], openedIds.Add, retryIds.Add, removedIds.Add);
        var prompt = Ui.Input("Use these references to explain the design.", true); prompt.MinHeight = 80;
        AttachmentDraftView.EnableFileDrop(prompt, () => true, _ => { });
        var textPaste = new DataObject(); textPaste.SetData(DataFormats.UnicodeText, "https://example.test/reference");
        var filePaste = new DataObject(); filePaste.SetData(DataFormats.FileDrop, new[] { @"C:\fixture\image.png" });
        var pastedBitmap = BitmapSource.Create(2, 2, 96, 96, PixelFormats.Bgra32, null,
            new byte[] { 0, 0, 255, 255, 0, 255, 0, 255, 255, 0, 0, 255, 255, 255, 255, 255 }, 8);
        var imagePaste = new DataObject(); imagePaste.SetData(DataFormats.Bitmap, pastedBitmap);
        var encoded = OrchestratorWindow.EncodeClipboardImage(pastedBitmap);
        Check(OrchestratorWindow.ClassifyClipboardPaste(textPaste) == OrchestratorWindow.ClipboardPasteKind.Text &&
            OrchestratorWindow.ClassifyClipboardPaste(filePaste) == OrchestratorWindow.ClipboardPasteKind.Files &&
            OrchestratorWindow.ClassifyClipboardPaste(imagePaste) == OrchestratorWindow.ClipboardPasteKind.Image &&
            encoded.Length > 8 && encoded.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }),
            "Clipboard text remains editable text while file drops and bounded in-memory bitmaps route through typed attachments.");
        var history = (TextBox)AttachmentPresentation.Reference(reference, "Image");
        Check(history.IsReadOnly && history.Text.Contains("diagram.png", StringComparison.Ordinal) && history.Text.Contains("12.2 KiB", StringComparison.Ordinal),
            "History presents readable filename/type/size references.");
        var surface = new DockPanel { Margin = new Thickness(24) };
        var captureRoot = new Border { Background = Ui.Surface, Child = surface };
        var heading = Ui.Text("Attachment controls · isolated native fixture", 20); DockPanel.SetDock(heading, Dock.Top); surface.Children.Add(heading);
        var composer = new StackPanel();
        composer.Children.Add(Ui.Text("Message Cerebral", 12)); composer.Children.Add(prompt); composer.Children.Add(view);
        composer.Children.Add(Ui.Bar(Ui.Button("Send", () => { }), Ui.Button("Attach", () => { })));
        DockPanel.SetDock(composer, Dock.Bottom); surface.Children.Add(composer); surface.Children.Add(history);
        var shown = new Window { Title = "Cerebral attachment controls — diagnostic fixture", Width = 1024, Height = 768,
            Content = captureRoot, WindowStartupLocation = WindowStartupLocation.CenterScreen, Background = Brushes.White };
        try
        {
            shown.Show(); await dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle); shown.UpdateLayout();
            Check(shown.IsVisible && view.ActualHeight is > 0 and <= 95 && view.ActualWidth > 0 && prompt.ActualHeight >= 80 && prompt.AllowDrop,
                "Actual 1024x768 fixture shows bounded native draft rows alongside a usable file-drop prompt.");
            var rows = (StackPanel)view.Content;
            var firstControls = (StackPanel)((DockPanel)((Border)rows.Children[0]).Child).Children[0];
            var secondControls = (StackPanel)((DockPanel)((Border)rows.Children[1]).Child).Children[0];
            ((Button)firstControls.Children[0]).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            ((Button)firstControls.Children[1]).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            ((Button)secondControls.Children[1]).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(openedIds.SequenceEqual(new[] { "image-import" }) && removedIds.SequenceEqual(new[] { "image-import" }) && retryIds.SequenceEqual(new[] { "text-import" }),
                "Actual native buttons return the selected stable import IDs.");
            Check(AutomationProperties.GetAutomationId(view) == "ComposerAttachments", "Native draft list exposes its diagnostic identity.");
            if (previewPath is not null)
            {
                var bitmap = new RenderTargetBitmap((int)Math.Ceiling(captureRoot.ActualWidth), (int)Math.Ceiling(captureRoot.ActualHeight), 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(captureRoot); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var output = new FileStream(previewPath, FileMode.Create, FileAccess.Write); encoder.Save(output);
            }
        }
        finally { shown.Close(); }
        view.Present([], openedIds.Add, retryIds.Add, removedIds.Add);
        Check(view.Visibility == Visibility.Collapsed && ((StackPanel)view.Content).Children.Count == 0,
            "Cleared selection collapses without retaining hidden draft controls.");
        Console.WriteLine($"Attachment composer: {checks} selection/host/native-view checks passed; isolated 1024x768 shown controls, no ConnectedWindow, service or model inference.");
        return checks;
    }
}
