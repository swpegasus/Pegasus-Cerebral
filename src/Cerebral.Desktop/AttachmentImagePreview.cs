using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Cerebral.Contracts;
using Cerebral.Contracts.Resources;
using Cerebral.Core;

namespace Cerebral.Desktop;

/// <summary>One bounded decoded preview of a scoped immutable original; never resolves a path or URL.</summary>
internal sealed class AttachmentImagePreview(AttachmentReference reference, byte[] bytes, BitmapSource image) : IDisposable
{
    internal const int MaximumBytes = 8 * 1024 * 1024;
    internal const int MaximumDisplayDimension = 1600;
    internal AttachmentReference Reference { get; } = reference;
    internal byte[] Bytes { get; } = bytes;
    internal BitmapSource Image { get; } = image;
    internal static (int Width, int Height) Dimensions(AttachmentReference reference)
    {
        int Read(string key) => reference.Metadata is { } metadata && metadata.TryGetValue(key, out var text) &&
            int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var value) ? value : 0;
        var width = Read("image.width"); var height = Read("image.height");
        if (width is < 1 or > 16_384 || height is < 1 or > 16_384 || (long)width * height > 16_000_000)
            throw new InvalidDataException("Image dimensions are missing or exceed this preview's 16-million-pixel limit.");
        return (width, height);
    }
    internal static bool SameReference(AttachmentReference expected, AttachmentReference actual, bool metadata = true) =>
        expected.Id == actual.Id && expected.Revision == actual.Revision && expected.MediaType == actual.MediaType &&
        expected.OriginalName == actual.OriginalName && expected.Length == actual.Length &&
        (!metadata || expected.Metadata is null || expected.Metadata.Count == actual.Metadata?.Count &&
            expected.Metadata.All(pair => actual.Metadata!.TryGetValue(pair.Key, out var value) && value == pair.Value));

    internal static async Task<AttachmentImagePreview> LoadAsync(AttachmentReference expected, string profileId,
        string conversationId, Func<CommandRequest, CancellationToken, Task<CommandResult>> command, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (expected.MediaType is not ("image/png" or "image/jpeg") || expected.Length is < 1 or > MaximumBytes)
            throw new InvalidDataException("Image preview supports admitted PNG/JPEG originals up to 8 MiB.");
        var described = await command(new("resources.attachments.describe", CerebralJson.Element(
            new AttachmentDescribeRequest(profileId, conversationId, expected.Id, expected.Revision))), token).ConfigureAwait(false);
        if (!described.Success) throw new InvalidDataException("Image preview is unavailable in this conversation.");
        var canonical = CerebralJson.Read<AttachmentReference>(described.Data);
        if (!SameReference(expected, canonical)) throw new InvalidDataException("Image identity changed; refresh its original receipt.");
        var dimensions = Dimensions(canonical);
        var bytes = new byte[checked((int)canonical.Length)];
        try
        {
            for (var offset = 0; offset < bytes.Length;)
            {
                token.ThrowIfCancellationRequested();
                var count = Math.Min(256 * 1024, bytes.Length - offset);
                var response = await command(new("resources.attachments.read", CerebralJson.Element(
                    new AttachmentRangeRequest(profileId, conversationId, canonical.Id, canonical.Revision, offset, count))), token).ConfigureAwait(false);
                if (!response.Success) throw new InvalidDataException("Image bytes are unavailable in this conversation.");
                var range = CerebralJson.Read<AttachmentRange>(response.Data);
                if (!SameReference(canonical, range.Attachment) || range.Offset != offset || range.Bytes.Length != count)
                    throw new InvalidDataException("Image range does not match the immutable original and exact requested interval.");
                range.Bytes.Span.CopyTo(bytes.AsSpan(offset)); offset += count;
            }
            token.ThrowIfCancellationRequested();
            var image = await Task.Run(() => Decode(bytes, canonical.MediaType, dimensions), token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            return new(canonical, bytes, image);
        }
        catch { Array.Clear(bytes); throw; }
    }

    private static BitmapSource Decode(byte[] bytes, string mediaType, (int Width, int Height) dimensions)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation | BitmapCreateOptions.IgnoreColorProfile,
            BitmapCacheOption.None);
        if (decoder.Frames.Count != 1 || !decoder.CodecInfo.MimeTypes.Split(',').Contains(mediaType, StringComparer.OrdinalIgnoreCase) ||
            decoder.Frames[0].PixelWidth != dimensions.Width || decoder.Frames[0].PixelHeight != dimensions.Height)
            throw new InvalidDataException("Decoded image does not match its admitted type or dimensions.");
        stream.Position = 0;
        var bitmap = new BitmapImage(); bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad; bitmap.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
        if (dimensions.Width >= dimensions.Height) bitmap.DecodePixelWidth = Math.Min(dimensions.Width, MaximumDisplayDimension);
        else bitmap.DecodePixelHeight = Math.Min(dimensions.Height, MaximumDisplayDimension);
        bitmap.StreamSource = stream; bitmap.EndInit(); bitmap.Freeze();
        if (bitmap.PixelWidth > MaximumDisplayDimension || bitmap.PixelHeight > MaximumDisplayDimension)
            throw new InvalidDataException("Decoded preview exceeded the display limit.");
        return bitmap;
    }
    public void Dispose() => Array.Clear(Bytes);
}

internal sealed class AttachmentImageCard : IDisposable
{
    private readonly CancellationTokenSource lifetime;
    private readonly Func<AttachmentReference, CancellationToken, Task<AttachmentImagePreview>> load;
    private readonly Action<AttachmentImagePreview> open;
    private readonly Image thumbnail = new() { Width = 150, Height = 100, Stretch = Stretch.Uniform, Visibility = Visibility.Collapsed };
    private readonly Button inspect;
    private readonly TextBlock state = Ui.Text("Preview not loaded.", 11, Ui.Muted);
    private Task? loading;
    private bool attempted;
    private bool disposed;
    internal Border View { get; }
    internal AttachmentImagePreview? Preview { get; private set; }
    internal DateTimeOffset LoadedAt { get; private set; }
    internal AttachmentReference Reference { get; }
    internal string Label { get; }
    internal string Status => state.Text;

    internal AttachmentImageCard(AttachmentReference reference, string label,
        Func<AttachmentReference, CancellationToken, Task<AttachmentImagePreview>> load,
        Action<AttachmentImagePreview> open, CancellationToken token)
    {
        Reference = reference; Label = label; this.load = load; this.open = open;
        lifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
        var body = new StackPanel();
        body.Children.Add(AttachmentPresentation.Reference(reference, label));
        var identity = Ui.Text(reference.Id + " · revision " + reference.Revision + " · " + reference.MediaType, 11, Ui.Muted);
        identity.TextWrapping = TextWrapping.Wrap; body.Children.Add(identity);
        inspect = Ui.Button("Show image", () =>
        {
            if (Preview is { } current) this.open(current);
            else _ = EnsurePreviewAsync(retry: true);
        });
        AutomationProperties.SetAutomationId(inspect, "WorkflowImageInspect");
        AutomationProperties.SetAutomationId(thumbnail, "WorkflowImageThumbnail");
        body.Children.Add(thumbnail); body.Children.Add(state); body.Children.Add(inspect);
        View = new Border { Child = body, Margin = new Thickness(0, 4, 0, 4), Padding = new Thickness(8),
            BorderBrush = Ui.Line, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4) };
        AutomationProperties.SetAutomationId(View, "WorkflowImageCard");
        View.Loaded += (_, _) => _ = EnsurePreviewAsync();
    }

    internal Task EnsurePreviewAsync(bool retry = false)
    {
        if (disposed || Preview is not null || attempted && !retry) return Task.CompletedTask;
        if (loading is not null) return loading;
        attempted = true;
        var flight = LoadAsync();
        if (!flight.IsCompleted) loading = flight;
        return flight;
    }
    private async Task LoadAsync()
    {
        inspect.IsEnabled = false; state.Text = "Loading validated image…";
        try
        {
            var preview = await load(Reference, lifetime.Token);
            if (disposed || lifetime.IsCancellationRequested) { preview.Dispose(); return; }
            Preview = preview; LoadedAt = DateTimeOffset.UtcNow; thumbnail.Source = preview.Image; thumbnail.Visibility = Visibility.Visible;
            var (width, height) = AttachmentImagePreview.Dimensions(preview.Reference);
            state.Text = width + " × " + height + " pixels · preview " + preview.Image.PixelWidth + " × " + preview.Image.PixelHeight;
            inspect.Content = "Open image";
        }
        catch (OperationCanceledException) { if (!disposed) state.Text = "Image preview canceled."; }
        catch (Exception error) when (error is InvalidDataException or IOException or FileFormatException or ArgumentException or InvalidOperationException or NotSupportedException or JsonException or System.Runtime.InteropServices.COMException)
        {
            if (!disposed) { ReleasePreview(); state.Text = "Preview unavailable: " + WorkflowWorkspace.ImagePreviewError(error.Message); inspect.Content = "Retry preview"; }
        }
        finally { loading = null; if (!disposed) inspect.IsEnabled = true; }
    }
    internal void ReleasePreview()
    {
        Preview?.Dispose(); Preview = null; thumbnail.Source = null; thumbnail.Visibility = Visibility.Collapsed;
        state.Text = "Preview released; open to reload."; inspect.Content = "Show image";
    }
    public void Dispose()
    {
        if (disposed) return;
        disposed = true; lifetime.Cancel(); ReleasePreview(); inspect.IsEnabled = false; state.Text = "Image preview closed.";
        lifetime.Dispose();
    }
}
