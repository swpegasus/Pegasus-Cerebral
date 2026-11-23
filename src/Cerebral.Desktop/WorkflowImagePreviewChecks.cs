using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Cerebral.Client;
using Cerebral.Contracts;
using Cerebral.Contracts.Workflows;
using Cerebral.Contracts.Resources;
using Cerebral.Core;

namespace Cerebral.Desktop;

/// <summary>Deterministic scoped-byte and WPF image fixtures; no live provider, model, network or shown window.</summary>
internal static class WorkflowImagePreviewChecks
{
    internal static async Task<int> CheckAsync()
    {
        var checks = 0;
        void Check(bool condition, string label) { if (!condition) throw new InvalidOperationException(label); checks++; }
        var pixels = new byte[3 * 2 * 4]; for (var index = 3; index < pixels.Length; index += 4) pixels[index] = 255;
        var bitmap = BitmapSource.Create(3, 2, 96, 96, PixelFormats.Bgra32, null, pixels, 12);
        using var encoded = new MemoryStream();
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); encoder.Save(encoded);
        var bytes = encoded.ToArray();
        var reference = new AttachmentReference("fixture-image", 2, "image/png", "café image.png", bytes.Length,
            new Dictionary<string, string> { ["image.width"] = "3", ["image.height"] = "2" });
        var calls = new List<CommandRequest>();
        var badIdentity = false; var badRange = false; var badDimensions = false; var malformedPixels = false;
        Task<CommandResult> Dispatch(CommandRequest request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); calls.Add(request);
            Check(request.ProfileId is null &&
                  request.Arguments.GetProperty("profileId").GetString() == "fixture-profile" &&
                  request.Arguments.GetProperty("conversationId").GetString() == "fixture-chat",
                "Image reads remain on the owner route and exact host-established profile/conversation.");
            var actual = badIdentity ? reference with { Id = "foreign-image" } :
                badDimensions ? reference with { Metadata = new Dictionary<string, string> { ["image.width"] = "4", ["image.height"] = "2" } } : reference;
            if (request.Operation == "resources.attachments.describe") return Task.FromResult(CommandResult.Ok(actual));
            if (request.Operation == "resources.attachments.read")
            {
                var offset = request.Arguments.GetProperty("offset").GetInt64(); var count = request.Arguments.GetProperty("count").GetInt32();
                Check(count <= 256 * 1024 && offset == 0 && count == bytes.Length, "Fixture preview uses one exact bounded range.");
                var returned = malformedPixels ? new byte[count] : bytes;
                return Task.FromResult(CommandResult.Ok(new AttachmentRange(actual, badRange ? 1 : offset, returned)));
            }
            throw new InvalidOperationException("An image preview cannot execute unrelated service commands.");
        }
        using var preview = await AttachmentImagePreview.LoadAsync(reference, "fixture-profile", "fixture-chat", Dispatch, CancellationToken.None);
        Check(preview.Reference.Id == reference.Id && preview.Image.IsFrozen && preview.Image.PixelWidth == 3 && preview.Image.PixelHeight == 2 &&
              preview.Bytes.SequenceEqual(bytes) && calls.Count == 2, "Preview pixels are derived from the exact immutable original.");
        var inspection = WorkflowWorkspace.CreateImageInspection(preview);
        Check(Find<Image>(inspection).Single().Source == preview.Image &&
              Find<TextBox>(inspection).Single().Text.Contains("café image.png", StringComparison.Ordinal) &&
              Find<TextBlock>(inspection).Any(text => text.Text.Contains("revision 2", StringComparison.Ordinal)),
            "Image inspection reuses the loaded bitmap and identifies the original name and revision without a new read.");

        using var jpegStream = new MemoryStream();
        var jpegEncoder = new JpegBitmapEncoder(); jpegEncoder.Frames.Add(BitmapFrame.Create(bitmap)); jpegEncoder.Save(jpegStream);
        var jpegBytes = jpegStream.ToArray();
        var jpegReference = reference with { MediaType = "image/jpeg", OriginalName = "fixture.jpg", Length = jpegBytes.Length };
        var jpegCalls = 0;
        using var jpeg = await AttachmentImagePreview.LoadAsync(jpegReference, "fixture-profile", "fixture-chat", (request, _) =>
        {
            jpegCalls++;
            return Task.FromResult(request.Operation == "resources.attachments.describe" ? CommandResult.Ok(jpegReference) :
                CommandResult.Ok(new AttachmentRange(jpegReference, 0, jpegBytes)));
        }, CancellationToken.None);
        Check(jpeg.Image.IsFrozen && jpeg.Image.PixelWidth == 3 && jpeg.Image.PixelHeight == 2 && jpegCalls == 2,
            "Both admitted PNG and JPEG originals decode into bounded frozen previews.");

        async Task Reject(AttachmentReference expected, string label)
        {
            var rejected = false;
            try { using var invalid = await AttachmentImagePreview.LoadAsync(expected, "fixture-profile", "fixture-chat", Dispatch, CancellationToken.None); }
            catch (Exception error) when (error is InvalidDataException or IOException or FileFormatException or ArgumentException or InvalidOperationException or NotSupportedException or JsonException or System.Runtime.InteropServices.COMException) { rejected = true; }
            Check(rejected, label);
        }
        badIdentity = true; await Reject(reference, "Changed immutable image identity is rejected before displaying pixels."); badIdentity = false;
        badRange = true; await Reject(reference, "An offset mismatch cannot be displayed as the requested image."); badRange = false;
        badDimensions = true; await Reject(reference with { Metadata = null }, "Decoded dimensions must agree with canonical admission metadata."); badDimensions = false;
        malformedPixels = true; await Reject(reference, "Malformed image bytes fail the preview without becoming a bitmap."); malformedPixels = false;
        var priorCalls = calls.Count;
        await Reject(reference with { MediaType = "text/html" }, "HTML and arbitrary URL content are not image previews.");
        await Reject(reference with { Length = AttachmentImagePreview.MaximumBytes + 1L }, "Oversized image originals are rejected before allocation.");
        Check(calls.Count == priorCalls, "Unsupported media and excessive lengths do not trigger byte requests.");
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        var cancellationObserved = false;
        try { using var ignored = await AttachmentImagePreview.LoadAsync(reference, "fixture-profile", "fixture-chat", Dispatch, cancelled.Token); }
        catch (OperationCanceledException) { cancellationObserved = true; }
        Check(cancellationObserved && calls.Count == priorCalls, "Canceled previews do not read or retain image bytes.");

        var opens = 0; var loads = 0; AttachmentImagePreview? opened = null;
        using var card = new AttachmentImageCard(reference, "Attached image", async (expected, token) =>
        {
            loads++; return await AttachmentImagePreview.LoadAsync(expected, "fixture-profile", "fixture-chat", Dispatch, token);
        }, loaded => { opens++; opened = loaded; }, CancellationToken.None);
        await card.EnsurePreviewAsync();
        var inspect = Find<Button>(card.View).Single(button => AutomationProperties.GetAutomationId(button) == "WorkflowImageInspect");
        inspect.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        inspect.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(loads == 1 && opens == 2 && ReferenceEquals(opened, card.Preview) &&
              Find<Image>(card.View).Single().Source == opened!.Image && card.Status.Contains("3 × 2", StringComparison.Ordinal),
            "Thumbnail and repeated inspection share the loaded validated preview.");
        var retainedBytes = card.Preview!.Bytes;
        card.ReleasePreview();
        Check(card.Preview is null && Find<Image>(card.View).Single().Source is null && retainedBytes.All(value => value == 0),
            "Releasing a cached preview clears its bytes and thumbnail while retaining original identity.");
        await card.EnsurePreviewAsync(retry: true);
        Check(loads == 2 && card.Preview is not null, "A released image can be explicitly reloaded from its exact coordinate.");
        card.Dispose();
        Check(card.Preview is null && !inspect.IsEnabled && card.Status == "Image preview closed.",
            "Disposed image cards disable inspection and release stale pixels.");

        var completion = new TaskCompletionSource<AttachmentImagePreview>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken pendingToken = default;
        using var staleCard = new AttachmentImageCard(reference, "Attached image", (_, token) =>
            { pendingToken = token; return completion.Task; }, _ => throw new InvalidOperationException("Stale previews cannot open."), CancellationToken.None);
        var pendingPreview = staleCard.EnsurePreviewAsync();
        staleCard.Dispose();
        var staleBytes = bytes.ToArray();
        completion.SetResult(new(reference, staleBytes, preview.Image));
        await pendingPreview;
        Check(pendingToken.IsCancellationRequested && staleBytes.All(value => value == 0) && staleCard.Preview is null &&
              Find<Image>(staleCard.View).Single().Source is null && staleCard.Status == "Image preview closed.",
            "A late preview completion after disposal is cleared rather than displayed in a newer conversation.");

        using var failedCard = new AttachmentImageCard(reference, "Captured image",
            (_, _) => Task.FromException<AttachmentImagePreview>(new InvalidDataException("fixture error password=do-not-show")),
            _ => throw new InvalidOperationException("A failed preview cannot open."), CancellationToken.None);
        await failedCard.EnsurePreviewAsync();
        Check(failedCard.Preview is null && failedCard.Status.Contains("[Redacted]", StringComparison.Ordinal) &&
              !failedCard.Status.Contains("do-not-show", StringComparison.Ordinal),
            "Failed preview state contains a bounded sanitized error and no stale bitmap.");
        var attempts = 0;
        using var retryCard = new AttachmentImageCard(reference, "Attached image",
            (_, _) => { attempts++; throw new InvalidDataException("fixture synchronous rejection"); }, _ => { }, CancellationToken.None);
        await retryCard.EnsurePreviewAsync(); await retryCard.EnsurePreviewAsync(); await retryCard.EnsurePreviewAsync(retry: true);
        Check(attempts == 2, "Automatic refresh does not loop a failed read; explicit retry still works after synchronous rejection.");

        const string presentationId = "11111111111111111111111111111111";
        var call = new CerebralEvent("capture-call", "fixture-chat", "fixture-turn", 1, CerebralEventTypes.ToolCall,
            new(EventActorKind.Agent), [], new(ModelInclusion.Required, HumanVisibility.Activity), new(EventSourceKind.Agent),
            DateTimeOffset.UnixEpoch, CerebralJson.Element(new ToolCallData("capture-tool-call", "cerebral.host:tool",
                CerebralJson.Element(new { name = "interface.status", arguments = new { requestId = presentationId } }))));
        CerebralEvent CaptureResult(object capture, string name = "interface.status", string status = "completed", string requestId = presentationId) =>
            new("capture-result", "fixture-chat", "fixture-turn", 2, CerebralEventTypes.ToolResult, new(EventActorKind.Tool),
                [new StructuredContentPart("workflow.command.result", CerebralJson.Element(new
                {
                    state = "Succeeded", data = new { name, content = JsonSerializer.Serialize(new { requestId, status, resource = capture }, CerebralJson.Options) }
                }))], new(ModelInclusion.Never, HumanVisibility.Activity), new(EventSourceKind.Tool), DateTimeOffset.UnixEpoch,
                CerebralJson.Element(new ToolResultData("capture-tool-call", "cerebral.host:tool", true)));
        var resource = new InterfaceResourceReceipt(reference.Id, reference.Revision, reference.MediaType, reference.OriginalName, reference.Length);
        var result = CaptureResult(resource);
        Check(WorkflowWorkspace.TryActivityCapture(new(call.Id, result, call), out var captureReference) &&
              AttachmentImagePreview.SameReference(reference, captureReference, metadata: false),
            "A paired successful interface.status receipt exposes only its exact owner-captured image coordinate.");
        Check(!WorkflowWorkspace.TryActivityCapture(new(result.Id, result), out _) &&
              !WorkflowWorkspace.TryActivityCapture(new(call.Id, CaptureResult(resource, name: "process.run"), call), out _) &&
              !WorkflowWorkspace.TryActivityCapture(new(call.Id, CaptureResult(resource, status: "pending"), call), out _) &&
              !WorkflowWorkspace.TryActivityCapture(new(call.Id, CaptureResult(resource, requestId: "22222222222222222222222222222222"), call), out _),
            "Orphan, unrelated, pending and mismatched receipts cannot manufacture an image capture.");
        Check(!WorkflowWorkspace.TryActivityCapture(new(call.Id, CaptureResult(new { path = @"C:\outside\image.png", url = "https://example.invalid/image.png" }), call), out _),
            "Returned paths and URLs never substitute for a validated immutable capture coordinate.");
        var conflicting = result with { Data = CerebralJson.Element(new ToolResultData("capture-tool-call", "cerebral.host:tool", false)) };
        var foreignTurn = result with { TurnId = "foreign-turn" };
        var foreignCall = result with { Data = CerebralJson.Element(new ToolResultData("different-call", "cerebral.host:tool", true)) };
        var repeatedReceipt = result with { Content = [.. result.Content, .. result.Content] };
        Check(!WorkflowWorkspace.TryActivityCapture(new(call.Id, conflicting, call), out _) &&
              !WorkflowWorkspace.TryActivityCapture(new(call.Id, foreignTurn, call), out _) &&
              !WorkflowWorkspace.TryActivityCapture(new(call.Id, foreignCall, call), out _) &&
              !WorkflowWorkspace.TryActivityCapture(new(call.Id, repeatedReceipt, call), out _),
            "Contradictory success, foreign operation identities and ambiguous capture receipts are rejected.");
        checks += await CheckWorkspaceAsync(reference, bytes, call, result);
        return checks;
    }

    private static async Task<int> CheckWorkspaceAsync(AttachmentReference reference, byte[] bytes,
        CerebralEvent call, CerebralEvent result)
    {
        var checks = 0;
        void Check(bool condition, string label) { if (!condition) throw new InvalidOperationException(label); checks++; }
        var dispatcher = Application.Current.Dispatcher;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        const string profileId = "fixture-profile";
        const string conversationId = "fixture-chat";
        const string runId = "fixture-turn";
        var original = new CerebralEvent("image-original", conversationId, runId, 1, CerebralEventTypes.Message,
            new(EventActorKind.User), [new TextContentPart("Inspect retained fixture images")],
            new(ModelInclusion.Required, HumanVisibility.Transcript), new(EventSourceKind.User), DateTimeOffset.UnixEpoch);
        var attached = new CerebralEvent("image-attached", conversationId, runId, 4, CerebralEventTypes.Message,
            new(EventActorKind.User), [new ImageContentPart(reference)], new(ModelInclusion.Required, HumanVisibility.Transcript),
            new(EventSourceKind.User), DateTimeOffset.UnixEpoch);
        IReadOnlyList<CerebralEvent> journal = [original, call with { Sequence = 2 }, result with { Sequence = 3 }, attached];
        var run = new WorkflowRun { Id = runId, Submission = new(runId, conversationId, profileId,
            ConversationControlClient.ChatDefinitionId, ConversationControlClient.ChatDefinitionVersion,
            "Inspect retained fixture images", null, WorkflowContract.EmptyObject, WorkflowContract.EmptyObject),
            Revision = 1, Status = WorkflowStatus.Cancelled, CancelRequested = true,
            CreatedAt = DateTimeOffset.UnixEpoch, UpdatedAt = DateTimeOffset.UnixEpoch };
        var resourceReads = 0;
        Task<CommandResult> Dispatch(CommandRequest request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Check(dispatcher.CheckAccess() && request.ProfileId is null, "Rendered image reads use the bound owner dispatcher route.");
            switch (request.Operation)
            {
                case "jobs.workflow.get": return Task.FromResult(CommandResult.Ok(run));
                case "jobs.workflow.recent":
                    Check(request.ProfileId is null && request.Arguments.ValueKind == JsonValueKind.Object &&
                          request.Arguments.EnumerateObject().Count() == 1 &&
                          request.Arguments.TryGetProperty("limit", out var recentLimit) &&
                          recentLimit.ValueKind == JsonValueKind.Number && recentLimit.TryGetInt32(out var boundedLimit) && boundedLimit == 40,
                        "Standalone image Work reads only the exact bounded recent page without changing its selected head.");
                    return Task.FromResult(CommandResult.Ok(new WorkflowRecentPage([], 0, null)));
                case "conversations.get":
                    Check(request.Arguments.GetProperty("id").GetString() == conversationId, "The image fixture preserves its exact conversation head.");
                    return Task.FromResult(CommandResult.Ok(new ConversationSummary(conversationId, "Image fixture", profileId, null,
                        runId, run.Status, run.UpdatedAt)));
                case "jobs.workflow.events.previous": return Task.FromResult(CommandResult.Ok(new
                    { events = journal, snapshotSequence = 4, nextBeforeSequence = (long?)null }));
                case "resources.attachments.describe":
                    resourceReads++;
                    Check(request.Arguments.GetProperty("profileId").GetString() == profileId &&
                          request.Arguments.GetProperty("conversationId").GetString() == conversationId &&
                          request.Arguments.GetProperty("id").GetString() == reference.Id &&
                          request.Arguments.GetProperty("revision").GetInt64() == reference.Revision,
                        "The native image card establishes exact profile, conversation and immutable coordinate.");
                    return Task.FromResult(CommandResult.Ok(reference));
                case "resources.attachments.read":
                    resourceReads++;
                    Check(request.Arguments.GetProperty("profileId").GetString() == profileId &&
                          request.Arguments.GetProperty("conversationId").GetString() == conversationId &&
                          request.Arguments.GetProperty("offset").GetInt64() == 0 &&
                          request.Arguments.GetProperty("count").GetInt32() == bytes.Length,
                        "The native image card reads only the canonical stored range.");
                    return Task.FromResult(CommandResult.Ok(new AttachmentRange(reference, 0, bytes)));
                default: throw new InvalidOperationException("Image fixtures cannot execute service mutations or model work: " + request.Operation);
            }
        }
        // Inline operation images belong to standalone Work; embedded Chat exposes only its Activity summary action.
        using var workspace = new WorkflowWorkspace(Dispatch, () => Array.Empty<AgentProfile>(),
            (message, error) => { if (error) throw new InvalidOperationException(message); }, embedded: false);
        using var source = new HwndSource(new HwndSourceParameters("Cerebral retained image checks")
        {
            Width = 680, Height = 480, WindowStyle = unchecked((int)0x80000000),
            PositionX = -32000, PositionY = -32000 // No WS_VISIBLE: this test never shows a user window.
        }) { RootVisual = workspace };
        void Layout()
        {
            workspace.Measure(new Size(680, 480)); workspace.Arrange(new Rect(0, 0, 680, 480)); workspace.UpdateLayout();
        }
        async Task Settle()
        {
            Layout(); await dispatcher.InvokeAsync(Layout, DispatcherPriority.ContextIdle, deadline.Token); Layout();
        }
        await workspace.OpenRunSnapshotAsync(runId, deadline.Token);
        await Settle();
        var cards = Rendered<Border>(workspace).Where(border => AutomationProperties.GetAutomationId(border) == "WorkflowImageCard").ToArray();
        var row = Rendered<Expander>(workspace).Single(expander => expander.Header is string header && header.Contains("interface.status", StringComparison.Ordinal));
        while (cards.Length == 2 && cards.Any(card => Rendered<Image>(card).Single().Source is null))
        {
            await Task.Delay(10, deadline.Token); await Settle();
        }
        Check(cards.Length == 2 && resourceReads == 4 && cards.All(card => Rendered<Image>(card).Single().Source is BitmapSource),
            "Retained attachments and exact capture receipts each render one validated thumbnail without inference.");
        Check(!row.IsExpanded && Rendered<Border>(row).Contains(cards.Single(card =>
                  Find<TextBox>(card).Single().Text.StartsWith("Captured image", StringComparison.Ordinal))) &&
              cards.Any(card => Find<TextBox>(card).Single().Text.StartsWith("Attached image", StringComparison.Ordinal)),
            "The captured image remains visible in its collapsed activity header and the attached image uses truthful distinct labeling.");
        row.IsExpanded = true;
        await Settle();
        var thumbnails = cards.Select(card => Rendered<Image>(card).Single()).ToArray();
        run = run with { Revision = 2 };
        await workspace.RefreshAsync();
        await Settle();
        Check(row.IsExpanded && ReferenceEquals(row, Rendered<Expander>(workspace).Single(expander =>
                  expander.Header is string header && header.Contains("interface.status", StringComparison.Ordinal))) &&
              cards.All(card => Rendered<Border>(workspace).Contains(card)) && thumbnails.All(image => image.Source is not null) && resourceReads == 4,
            "Refreshing preserves image controls, expanded operation state and loaded previews without duplicate reads.");
        workspace.Reset();
        await Settle();
        Check(thumbnails.All(image => image.Source is null) && cards.All(card => !Find<Button>(card).Single().IsEnabled) &&
              !Rendered<Border>(workspace).Any(border => AutomationProperties.GetAutomationId(border) == "WorkflowImageCard"),
            "Reset releases stale thumbnails and disables the old controls before another conversation can render.");
        return checks;
    }

    private static IEnumerable<T> Rendered<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is T found) yield return found;
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
            foreach (var nested in Rendered<T>(VisualTreeHelper.GetChild(root, index))) yield return nested;
    }

    private static IEnumerable<T> Find<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is T found) yield return found;
        if (root is Panel panel) foreach (var child in panel.Children.OfType<DependencyObject>()) foreach (var nested in Find<T>(child)) yield return nested;
        else if (root is Border { Child: { } borderChild }) foreach (var nested in Find<T>(borderChild)) yield return nested;
        else if (root is ScrollViewer { Content: DependencyObject scrollChild }) foreach (var nested in Find<T>(scrollChild)) yield return nested;
    }
}
