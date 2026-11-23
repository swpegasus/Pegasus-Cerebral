using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using Cerebral.Contracts;
using Cerebral.Contracts.Resources;
using Cerebral.Core;

namespace Cerebral.Desktop;

public sealed partial class WorkflowWorkspace
{
    private const int MaximumImageCards = 32;
    private const long MaximumLoadedImageBytes = 64 * 1024 * 1024;
    private readonly Dictionary<string, AttachmentImageCard> imageCards = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Window> imageWindows = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim imagePreviewGate = new(1, 1);
    private int imagePreviewLoads;
    private static readonly DependencyProperty ActivityImageHeaderProperty = DependencyProperty.RegisterAttached(
        "ActivityImageHeader", typeof(UIElement), typeof(WorkflowWorkspace), new PropertyMetadata(null));

    internal static bool TryActivityCapture(ActivityRow row, out AttachmentReference reference)
    {
        reference = null!;
        if (row.Call is null || row.Event.Type != CerebralEventTypes.ToolResult ||
            !TryActivityOperationIdentity(row.Call, out var callIdentity) ||
            !TryActivityOperationIdentity(row.Event, out var resultIdentity) || callIdentity != resultIdentity ||
            callIdentity.ToolName != "cerebral.host:tool" || ToolResult(row.Event).Success != true ||
            !ActivityCommand(row.Call, out var tool, out var arguments, out _) || tool != "interface.status" ||
            arguments.ValueKind != JsonValueKind.Object ||
            !arguments.TryGetProperty("requestId", out var requested) || requested.ValueKind != JsonValueKind.String)
            return false;
        var results = row.Event.Content.OfType<StructuredContentPart>().Where(part => part.Schema == "workflow.command.result").ToArray();
        if (results.Length != 1 || results[0].Value is not { ValueKind: JsonValueKind.Object } value ||
            value.EnumerateObject().Select(field => field.Name).Distinct(StringComparer.Ordinal).Count() != value.EnumerateObject().Count() ||
            !value.TryGetProperty("state", out var state) || state.ValueKind != JsonValueKind.String || state.GetString() != "Succeeded" ||
            !value.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object ||
            data.EnumerateObject().Select(field => field.Name).Distinct(StringComparer.Ordinal).Count() != data.EnumerateObject().Count() ||
            !data.TryGetProperty("name", out var name) || name.ValueKind != JsonValueKind.String || name.GetString() != "interface.status" ||
            !data.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.String ||
            content.GetString() is not { Length: > 0 and <= 65_536 } text) return false;
        try
        {
            using var parsed = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 12 });
            var status = parsed.RootElement;
            if (status.ValueKind != JsonValueKind.Object ||
                status.EnumerateObject().Select(field => field.Name).Distinct(StringComparer.Ordinal).Count() != status.EnumerateObject().Count() ||
                !status.TryGetProperty("requestId", out var requestId) || requestId.ValueKind != JsonValueKind.String ||
                !Guid.TryParseExact(requestId.GetString(), "N", out var id) || id == Guid.Empty ||
                requested.GetString() != "reviewed" && requested.GetString() != requestId.GetString() ||
                !status.TryGetProperty("status", out var reportedState) || reportedState.ValueKind != JsonValueKind.String ||
                reportedState.GetString() is not ("completed" or "failed") ||
                !status.TryGetProperty("resource", out var resource) || resource.ValueKind != JsonValueKind.Object ||
                resource.EnumerateObject().Select(field => field.Name).Distinct(StringComparer.Ordinal).Count() != resource.EnumerateObject().Count())
                return false;
            var receipt = CerebralJson.Read<InterfaceResourceReceipt>(resource);
            if (receipt.MediaType is not ("image/png" or "image/jpeg") || receipt.Id is not { Length: > 0 and <= 128 } ||
                receipt.Id.Any(character => char.IsWhiteSpace(character) || char.IsControl(character)) ||
                receipt.Revision < 1 || receipt.Length is < 1 or > 16 * 1024 * 1024 ||
                receipt.OriginalName is not { Length: > 0 and <= 1024 }) return false;
            reference = new(receipt.Id, receipt.Revision, receipt.MediaType, receipt.OriginalName, receipt.Length);
            return true;
        }
        catch (Exception error) when (error is JsonException or ArgumentException or InvalidOperationException) { return false; }
    }

    internal static string ImagePreviewError(string message) => BoundActivitySummary(message);

    private string ImageCardKey(CerebralEvent item, string suffix) =>
        Snapshot?.Run?.ProfileId + "|" + item.ConversationId + "|" + item.Id + "|" + suffix;

    private void ReconcileImageWindow()
    {
        var retained = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in displayedEvents)
            for (var index = 0; index < item.Content.Count; index++)
                if (item.Content[index] is ImageContentPart) retained.Add(ImageCardKey(item, "image-" + index));
        foreach (var row in ProjectActivityRows(displayedEvents))
            if (TryActivityCapture(row, out _)) retained.Add(ImageCardKey(row.Event, "capture"));
        foreach (var key in imageCards.Keys.Where(key => !retained.Contains(key)).ToArray())
        {
            CloseImageWindow(key); imageCards[key].Dispose(); imageCards.Remove(key);
        }
    }

    private FrameworkElement ImageCard(CerebralEvent item, AttachmentReference reference, string label, string suffix)
    {
        var key = ImageCardKey(item, suffix);
        if (Snapshot?.Run is not { } run || item.ConversationId != Snapshot.ConversationId)
            return AttachmentPresentation.Reference(reference, label + " · inspection needs its original conversation");
        if (!imageCards.TryGetValue(key, out var card))
        {
            if (imageCards.Count >= MaximumImageCards)
                return AttachmentPresentation.Reference(reference, label + " · current preview window is full");
            var profileId = run.ProfileId; var conversationId = item.ConversationId; var bound = BoundCommand();
            card = new(reference, label, (expected, token) => LoadImagePreviewAsync(expected, profileId, conversationId, bound, token),
                preview => OpenImageWindow(key, preview), lifetime.Token);
            imageCards.Add(key, card);
        }
        if (card.View.Parent is Panel parent) parent.Children.Remove(card.View);
        return card.View;
    }

    private async Task<AttachmentImagePreview> LoadImagePreviewAsync(AttachmentReference reference, string profileId,
        string conversationId, Func<CommandRequest, CancellationToken, Task<CommandResult>> bound, CancellationToken token)
    {
        if (disposed) throw new ObjectDisposedException(nameof(WorkflowWorkspace));
        if (reference.MediaType is not ("image/png" or "image/jpeg") || reference.Length is < 1 or > AttachmentImagePreview.MaximumBytes)
            throw new InvalidDataException("Image preview supports admitted PNG/JPEG originals up to 8 MiB.");
        imagePreviewLoads++;
        var acquired = false;
        try
        {
            await imagePreviewGate.WaitAsync(token); acquired = true;
            foreach (var cached in imageCards.Values.Where(card => card.Preview is not null).OrderBy(card => card.LoadedAt).ToArray())
            {
                if (imageCards.Values.Sum(card => (long)(card.Preview?.Bytes.Length ?? 0)) + reference.Length <= MaximumLoadedImageBytes) break;
                // Thumbnails are disposable UI data; the original is still in the scoped store.
                var key = imageCards.First(pair => ReferenceEquals(pair.Value, cached)).Key;
                CloseImageWindow(key); cached.ReleasePreview();
            }
            token.ThrowIfCancellationRequested();
            return await AttachmentImagePreview.LoadAsync(reference, profileId, conversationId, bound, token);
        }
        finally
        {
            if (acquired) imagePreviewGate.Release();
            if (--imagePreviewLoads == 0 && disposed) imagePreviewGate.Dispose();
        }
    }

    private static void AppendActivityImageHeader(FrameworkElementFactory layout)
    {
        var image = new FrameworkElementFactory(typeof(ContentPresenter));
        image.SetBinding(ContentPresenter.ContentProperty, new Binding
        {
            Path = new PropertyPath("(0)", ActivityImageHeaderProperty),
            RelativeSource = new RelativeSource(RelativeSourceMode.FindAncestor, typeof(Expander), 1)
        });
        layout.AppendChild(image);
    }

    private void RenderActivityImageHeader(Expander expander, ActivityRow row)
    {
        var card = TryActivityCapture(row, out var reference) ? ImageCard(row.Event, reference, "Captured image", "capture") : null;
        if (!ReferenceEquals(expander.GetValue(ActivityImageHeaderProperty), card))
            expander.SetValue(ActivityImageHeaderProperty, card);
    }

    private void OpenImageWindow(string key, AttachmentImagePreview preview)
    {
        if (disposed || !imageCards.TryGetValue(key, out var card) || !ReferenceEquals(card.Preview, preview)) return;
        if (imageWindows.TryGetValue(key, out var existing)) { existing.Activate(); return; }
        if (imageWindows.Count >= 4) CloseImageWindow(imageWindows.Keys.First());
        var body = CreateImageInspection(preview);
        var owner = Window.GetWindow(this);
        var window = new Window { Owner = owner, Title = preview.Reference.OriginalName + " · Pegasus Cerebral",
            Width = 900, Height = 700, Content = body, WindowStartupLocation = owner is null ?
                WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner };
        window.Closed += (_, _) => imageWindows.Remove(key);
        imageWindows.Add(key, window); WindowSizing.Attach(window); window.Show(); window.Activate();
    }

    internal static FrameworkElement CreateImageInspection(AttachmentImagePreview preview)
    {
        var content = new DockPanel { Margin = new Thickness(12) };
        var details = new StackPanel();
        details.Children.Add(AttachmentPresentation.Reference(preview.Reference, "Image original"));
        var (width, height) = AttachmentImagePreview.Dimensions(preview.Reference);
        details.Children.Add(Ui.Text(preview.Reference.Id + " · revision " + preview.Reference.Revision + " · " +
            preview.Reference.MediaType + " · " + width + " × " + height + " pixels", 12));
        details.Children.Add(Ui.Text("Display preview: " + preview.Image.PixelWidth + " × " + preview.Image.PixelHeight +
            " pixels. The stored original is unchanged.", 11, Ui.Muted));
        DockPanel.SetDock(details, Dock.Top); content.Children.Add(details);
        var image = new Image { Source = preview.Image, Stretch = Stretch.None, HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top };
        content.Children.Add(new ScrollViewer { Content = image, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        return content;
    }
    private void CloseImageWindow(string key)
    {
        if (imageWindows.Remove(key, out var window)) window.Close();
    }
    private void ClearImageCards()
    {
        foreach (var key in imageWindows.Keys.ToArray()) CloseImageWindow(key);
        foreach (var card in imageCards.Values) card.Dispose();
        imageCards.Clear();
        if (disposed && imagePreviewLoads == 0) imagePreviewGate.Dispose();
    }
}
