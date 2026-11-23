using System.IO;
using System.Diagnostics;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Cerebral.Client;
using Cerebral.Contracts;
using Cerebral.Contracts.Resources;
using Cerebral.Core;
using Cerebral.Desktop;
using Cerebral.Desktop.Plugins;
using Microsoft.Win32;

namespace Cerebral.Orchestrator;

public sealed partial class OrchestratorWindow
{
    internal enum ClipboardPasteKind { Text, Files, Image }

    private sealed class DraftAttachment(AttachmentTransferClient transfer, Stream? source, string? localPath,
        string displayName, long displayLength, string? preparedState = null, IDisposable? transport = null,
        byte[]? previewBytes = null) : IDisposable
    {
        public AttachmentTransferClient Transfer { get; } = transfer;
        public Stream? Source { get; private set; } = source;
        public string? LocalPath { get; } = localPath;
        public string DisplayName { get; } = displayName;
        public long DisplayLength { get; } = displayLength;
        public bool DerivedBusy { get; set; }
        public string? DerivedState { get; set; } = preparedState;
        public string? DerivedError { get; set; }
        public byte[]? PreviewBytes { get; private set; } = previewBytes;
        public void ReleaseSource() { Source?.Dispose(); Source = null; }
        public void Dispose() { ReleaseSource(); PreviewBytes = null; transport?.Dispose(); }
    }
    private readonly List<DraftAttachment> attachmentDrafts = [];
    private readonly Dictionary<string, AttachmentDraftSelection> attachmentSelections = new(StringComparer.Ordinal);
    private readonly AttachmentDraftView attachmentView = new();
    private (string ConversationId, string ProfileId)? attachmentReservedOwner;
    private bool attachmentInputInterception;
    private int pastedImageSequence;

    private void InitializeAttachmentComposer(StackPanel composer)
    {
        composer.Children.Add(attachmentView);
        AttachmentDraftView.EnableFileDrop(prompt, () => IsNativeChat && !NativeMutationPending && !NativeMutationUncertain,
            paths => Run(() => AddAttachmentFilesAsync(paths)));
        DataObject.AddPastingHandler(prompt, (_, args) =>
        {
            var kind = ClassifyClipboardPaste(args.SourceDataObject);
            if (kind == ClipboardPasteKind.Text) { ClipboardRichText.PreparePaste(args); return; }
            args.CancelCommand();
            if (kind == ClipboardPasteKind.Files && args.SourceDataObject.GetData(DataFormats.FileDrop) is string[] paths)
                Run(() => AddAttachmentFilesAsync(paths));
            else if (kind == ClipboardPasteKind.Image && args.SourceDataObject.GetData(DataFormats.Bitmap, true) is BitmapSource image)
            {
                var bytes = EncodeClipboardImage(image);
                Run(() => AddClipboardImageAsync(bytes));
            }
        });
        Closed += (_, _) => { foreach (var item in attachmentDrafts) item.Dispose(); };
    }

    internal static ClipboardPasteKind ClassifyClipboardPaste(IDataObject data) =>
        data.GetDataPresent(DataFormats.FileDrop) ? ClipboardPasteKind.Files :
        data.GetDataPresent(DataFormats.Bitmap, true) ? ClipboardPasteKind.Image : ClipboardPasteKind.Text;

    internal static byte[] EncodeClipboardImage(BitmapSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.PixelWidth is < 1 or > 16_384 || source.PixelHeight is < 1 or > 16_384 ||
            (long)source.PixelWidth * source.PixelHeight > 16_000_000)
            throw new InvalidDataException("The pasted image exceeds Cerebral's 16-megapixel input limit.");
        using var output = new MemoryStream();
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(source)); encoder.Save(output);
        if (output.Length > AttachmentTransferClient.MaximumFileBytes)
            throw new InvalidDataException("The pasted PNG exceeds Cerebral's 16 MiB attachment limit.");
        return output.ToArray();
    }

    private void PickAttachments()
    {
        var picker = new OpenFileDialog { Title = "Attach files or images", Multiselect = true,
            Filter = "Supported files|*.txt;*.md;*.markdown;*.csv;*.tsv;*.log;*.json;*.xml;*.yaml;*.yml;*.cs;*.py;*.js;*.ts;*.css;*.sql;*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tif;*.tiff;*.wdp;*.wmp;*.webp;*.zip;*.7z;*.rar;*.tar;*.gz;*.tgz;*.bz2;*.xz;*.docx;*.xlsx;*.pptx;*.jar|All files|*.*",
            CheckFileExists = true };
        if (picker.ShowDialog(this) == true) Run(() => AddAttachmentFilesAsync(picker.FileNames));
    }

    private void EnsureAttachmentDraftEditable()
    {
        if (!IsNativeChat) throw new InvalidOperationException("Use a new conversation to attach files.");
        if (NativeMutationPending || NativeMutationUncertain || attachmentInputInterception)
            throw new InvalidOperationException("Keep the selected files unchanged until the current request is confirmed.");
        if (desktopInputGuidanceHost.Visibility == Visibility.Visible)
            throw new InvalidOperationException("Finish or dismiss request review before changing its selected files.");
        if (attachmentDrafts.Any(x => x.Transfer.Snapshot.Busy || x.DerivedBusy))
            throw new InvalidOperationException("Wait for the current attachment transfer.");
    }

    private void EnsureAttachmentNavigationAllowed()
    {
        if (attachmentInputInterception || attachmentDrafts.Count != 0)
            throw new InvalidOperationException("Send or remove the selected attachments before changing conversations or service connections. Unconfirmed imports retain their original request IDs.");
    }

    private async Task AddAttachmentFilesAsync(IReadOnlyList<string> paths)
    {
        EnsureAttachmentDraftEditable();
        if (paths.Count is < 1 or > 16 || attachmentDrafts.Count + paths.Count > 16)
            throw new InvalidOperationException("A message can include at most 16 files or images.");
        var binding = PluginBinding(); var profile = ConversationProfile().Id; var connection = ServiceConnectionIdentity;
        var conversation = durableConversationId!;
        // Files stay in this user process. The service receives only bounded chunks, never a local path.
        foreach (var path in paths)
        {
            if (binding != PluginBinding() || connection != ServiceConnectionIdentity)
                throw new InvalidOperationException("The attachment destination changed before import.");
            Stream? source = null; CerebralClient? transport = null;
            try
            {
                var prepared = PrepareAttachmentSource(path);
                source = prepared.Source;
                var request = new AttachmentImportBeginRequest(Guid.NewGuid().ToString("N"), profile, conversation,
                    prepared.UploadName, source.Length);
                transport = CreateIndependentClient();
                var transfer = new AttachmentTransferClient(request, transport);
                var item = new DraftAttachment(transfer, source, prepared.LocalPath, prepared.DisplayName,
                    prepared.DisplayLength, prepared.State, transport); source = null; transport = null;
                attachmentDrafts.Add(item);
                attachmentReservedOwner = (conversation, profile);
                ResetAttachmentSelection(); RenderAttachmentDrafts();
                await TransferAttachmentAsync(item, remove: false);
            }
            finally { source?.Dispose(); transport?.Dispose(); }
        }
    }

    private async Task AddClipboardImageAsync(byte[] bytes)
    {
        EnsureAttachmentDraftEditable();
        if (attachmentDrafts.Count >= 16) throw new InvalidOperationException("A message can include at most 16 files or images.");
        if (bytes.Length is < 1 or > AttachmentTransferClient.MaximumFileBytes)
            throw new InvalidDataException("The pasted image is empty or exceeds Cerebral's 16 MiB attachment limit.");
        var binding = PluginBinding(); var profile = ConversationProfile().Id; var conversation = durableConversationId!;
        var number = ++pastedImageSequence;
        var name = number == 1 ? "Pasted image.png" : $"Pasted image {number}.png";
        var source = new MemoryStream(bytes, writable: false);
        CerebralClient? transport = null;
        try
        {
            transport = CreateIndependentClient();
            var request = new AttachmentImportBeginRequest(Guid.NewGuid().ToString("N"), profile, conversation, name, source.Length);
            var transfer = new AttachmentTransferClient(request, transport);
            var item = new DraftAttachment(transfer, source, null, name, source.Length, "Pasted from clipboard", transport,
                previewBytes: bytes.ToArray());
            source = null!; transport = null;
            attachmentDrafts.Add(item); attachmentReservedOwner = (conversation, profile);
            ResetAttachmentSelection(); RenderAttachmentDrafts();
            await TransferAttachmentAsync(item, remove: false);
        }
        finally { source?.Dispose(); transport?.Dispose(); }
    }

    private sealed record PreparedAttachment(Stream Source, string LocalPath, string DisplayName, long DisplayLength,
        string UploadName, string? State);

    private static PreparedAttachment PrepareAttachmentSource(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var info = new FileInfo(fullPath);
        var extension = info.Extension.ToLowerInvariant();
        if (extension is not (".bmp" or ".gif" or ".tif" or ".tiff" or ".wdp" or ".wmp" or ".webp"))
        {
            Stream file = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read,
                AttachmentTransferClient.MaximumChunkBytes, FileOptions.Asynchronous | FileOptions.SequentialScan);
            return new(file, fullPath, info.Name, info.Length, info.Name, null);
        }
        try
        {
            using var input = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            var decoder = BitmapDecoder.Create(input, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            if (decoder.Frames.Count == 0) throw new InvalidDataException("The image contains no decodable frame.");
            var frame = decoder.Frames[0];
            if (frame.PixelWidth is < 1 or > 16_384 || frame.PixelHeight is < 1 or > 16_384 ||
                (long)frame.PixelWidth * frame.PixelHeight > 16_000_000)
                throw new InvalidDataException("The image exceeds Cerebral's 16-megapixel input limit.");
            var output = new MemoryStream();
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(frame)); encoder.Save(output);
            if (output.Length > AttachmentTransferClient.MaximumFileBytes)
            { output.Dispose(); throw new InvalidDataException("The converted PNG exceeds Cerebral's 16 MiB attachment limit."); }
            output.Position = 0;
            return new(output, fullPath, info.Name, info.Length, Path.GetFileNameWithoutExtension(info.Name) + ".png", "Converted to PNG");
        }
        catch (Exception error) when (error is FileFormatException or NotSupportedException or InvalidOperationException)
        {
            throw new InvalidDataException($"Windows cannot decode '{info.Name}' for model input. Convert it to PNG or JPEG, or install a Windows imaging codec for this format.", error);
        }
    }

    private async Task TransferAttachmentAsync(DraftAttachment item, bool remove)
    {
        if (!attachmentDrafts.Contains(item)) return;
        EnsureAttachmentDraftEditable();
        if (remove && item.Transfer.Snapshot.Attachment is not null)
        {
            item.Dispose(); attachmentDrafts.Remove(item); ResetAttachmentSelection(); RenderAttachmentDrafts();
            SetStatus("Attachment removed from this draft. Saved originals are retained."); return;
        }
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(Lifetime.Token);
            deadline.CancelAfter(TimeSpan.FromMinutes(2));
            var work = remove ? item.Transfer.AbortAsync(deadline.Token) : item.Transfer.UploadAsync(item.Source, deadline.Token);
            RenderAttachmentDrafts();
            var receipt = await work;
            if (receipt.Attachment is not null) item.ReleaseSource();
            if (!remove && receipt.Attachment?.Metadata?.ContainsKey("handler.archive") == true)
                await PrepareArchiveAsync(item, receipt.Attachment, deadline.Token);
            if (remove && !receipt.Uncertain && receipt.Status?.State != "committing")
            {
                item.Dispose(); attachmentDrafts.Remove(item); ResetAttachmentSelection();
            }
            SetStatus(remove ? "Attachment removed from this draft. Saved originals are retained." :
                receipt.Attachment is not null && item.DerivedError is null ? "Attachment ready." :
                receipt.Attachment is not null ? "The original is retained, but its archive derivative needs attention." :
                "Attachment commit is in progress. Refresh its receipt.", item.DerivedError is not null);
        }
        catch (Exception error) when (error is IOException or InvalidOperationException or ArgumentException or
            System.Net.Http.HttpRequestException or OperationCanceledException or TimeoutException or System.Text.Json.JsonException)
        {
            var detail = error.Message.Contains("bounded object envelope", StringComparison.OrdinalIgnoreCase)
                ? "The image transfer exceeded an internal command limit."
                : error.Message;
            SetStatus("Attachment was not confirmed. " + detail + " Retry resumes the same file; the original is unchanged.", true);
        }
        finally { RenderAttachmentDrafts(); }
    }

    private async Task PrepareArchiveAsync(DraftAttachment item, AttachmentReference attachment, CancellationToken cancellationToken)
    {
        item.DerivedBusy = true; item.DerivedState = "Preparing archive…"; item.DerivedError = null; RenderAttachmentDrafts();
        try
        {
            using var client = CreateIndependentClient();
            var receipt = await client.GetAsync<AttachmentArtifactReceipt>(
                "resources.attachments.archive.extract",
                new AttachmentArchiveRequest(ConversationProfile().Id, durableConversationId!, attachment.Id, attachment.Revision),
                cancellationToken: cancellationToken);
            item.DerivedState = receipt.State == "ready"
                ? $"Ready · {receipt.Entries.Count} extracted item{(receipt.Entries.Count == 1 ? "" : "s")}" : receipt.State;
        }
        catch (Exception error) when (error is IOException or InvalidOperationException or ArgumentException or
            System.Net.Http.HttpRequestException or OperationCanceledException or TimeoutException or System.Text.Json.JsonException)
        {
            item.DerivedError = error.Message;
            item.DerivedState = "Archive needs attention";
        }
        finally { item.DerivedBusy = false; RenderAttachmentDrafts(); }
    }

    private void ResetAttachmentSelection()
    {
        // No accepted mutation is active when this is called. A changed draft receives a fresh run ID.
        attachmentSelections.Clear(); pendingSubmission = null;
    }

    private IReadOnlyList<ContentPart> FreezeAttachments(DesktopSubmission input, string profileId)
    {
        if (attachmentSelections.TryGetValue(input.RequestId, out var prior))
        {
            if (prior.Binding != input.Binding || prior.ProfileId != profileId)
                throw new InvalidOperationException("The retained attachment selection belongs to a different destination.");
            return prior.Content;
        }
        if (attachmentSelections.Count >= 32)
            throw new InvalidOperationException("Too many pending request revisions. Remove and reselect the draft attachments before creating another revision.");
        var selected = AttachmentDraftSelection.Freeze(input.Binding, profileId, attachmentDrafts.Select(x => x.Transfer.Snapshot).ToArray());
        attachmentSelections.Add(input.RequestId, selected);
        return selected.Content;
    }

    private bool HasFrozenAttachments(DesktopSubmission input) =>
        attachmentSelections.TryGetValue(input.RequestId, out var selected) &&
        selected.Binding == input.Binding && selected.Content.Count > 0;

    private void AcceptAttachmentSelection(string requestId)
    {
        if (!attachmentSelections.TryGetValue(requestId, out var selected)) return;
        foreach (var item in attachmentDrafts.Where(x => selected.ImportIds.Contains(x.Transfer.Snapshot.Request.RequestId)).ToArray())
        { item.Dispose(); attachmentDrafts.Remove(item); }
        attachmentSelections.Clear(); RenderAttachmentDrafts();
    }

    private void RenderAttachmentDrafts()
    {
        var locked = NativeMutationPending || NativeMutationUncertain || attachmentInputInterception ||
            desktopInputGuidanceHost.Visibility == Visibility.Visible || attachmentDrafts.Any(x => x.Transfer.Snapshot.Busy || x.DerivedBusy);
        var rows = attachmentDrafts.Select(item =>
        {
            var snapshot = item.Transfer.Snapshot;
            var state = snapshot.Busy ? "Transferring…" : item.DerivedState ?? (snapshot.Attachment is not null ? "Ready" :
                snapshot.Uncertain ? "Unconfirmed" : snapshot.Error is not null ? "Needs attention" : snapshot.Status?.State ?? "Selected");
            return new AttachmentDraftRow(snapshot.Request.RequestId, item.DisplayName, item.DisplayLength,
                state, item.DerivedError ?? snapshot.Error, item.PreviewBytes is not null || item.LocalPath is not null && File.Exists(item.LocalPath),
                !locked && (snapshot.Attachment is null || item.DerivedError is not null), !locked);
        }).ToArray();
        void Act(string id, bool remove)
        {
            if (attachmentDrafts.FirstOrDefault(x => x.Transfer.Snapshot.Request.RequestId == id) is { } item)
                Run(() => TransferAttachmentAsync(item, remove));
        }
        attachmentView.Present(rows, OpenAttachment, id => Act(id, false), id => Act(id, true));
    }

    private void OpenAttachment(string id)
    {
        var item = attachmentDrafts.FirstOrDefault(x => x.Transfer.Snapshot.Request.RequestId == id) ??
            throw new InvalidOperationException("The selected attachment is no longer in this draft.");
        if (item.PreviewBytes is { } pasted)
        {
            using var memory = new MemoryStream(pasted, writable: false);
            var image = new BitmapImage(); image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = memory; image.EndInit(); image.Freeze();
            ShowImagePreview(item.DisplayName, image); return;
        }
        if (item.LocalPath is null || !File.Exists(item.LocalPath))
            throw new FileNotFoundException("The original selected file is no longer available.", item.LocalPath);
        var extension = Path.GetExtension(item.LocalPath).ToLowerInvariant();
        if (extension is ".png" or ".jpg" or ".jpeg" or ".bmp" or ".gif" or ".tif" or ".tiff" or ".wdp" or ".wmp" or ".webp")
        {
            var image = new BitmapImage();
            image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad; image.UriSource = new Uri(item.LocalPath); image.EndInit(); image.Freeze();
            ShowImagePreview(item.DisplayName, image); return;
        }
        using var process = Process.Start(new ProcessStartInfo(item.LocalPath) { UseShellExecute = true }) ??
            throw new IOException("Windows could not open the selected file.");
    }

    private void ShowImagePreview(string name, ImageSource image)
    {
        var view = new Image { Source = image, Stretch = Stretch.Uniform, Margin = new Thickness(12) };
        var preview = new Window { Owner = this, Icon = this.Icon, Title = name + " · Pegasus Cerebral", Width = 900, Height = 700,
            Content = new ScrollViewer { Content = view, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto }, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        WindowSizing.Attach(preview); preview.Show(); preview.Activate();
    }
}
