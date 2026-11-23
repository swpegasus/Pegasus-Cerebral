using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Cerebral.ChatIconsPlugin;
using Cerebral.Desktop.Plugins;

namespace Cerebral.ChatIconsPlugin.Tests;

internal static class CortexIconLibraryChecks
{
    public static int Run()
    {
        var checks = 0;
        void Check(bool valid, string message) { if (!valid) throw new Exception(message); ++checks; }
        void Reject(Action action, string message)
        {
            try { action(); }
            catch (Exception error) when (error is InvalidDataException or IOException or FormatException or JsonException or ArgumentException or OperationCanceledException)
            { ++checks; return; }
            throw new Exception("Expected rejection: " + message);
        }
        var host = new ResourceHost();
        var library = new CortexIconLibrary(host);
        var providers = library.ProvidersAsync(default).GetAwaiter().GetResult();
        Check(providers.Count == 1 && providers[0].Transport == "cortex-native-pipe", "Native provider inventory.");
        host.Mutate = (_, result) => { result["providers"]![0]!["transport"] = "stdio"; };
        Reject(() => library.ProvidersAsync(default).GetAwaiter().GetResult(), "Unreviewed provider transport.");
        host.Mutate = null;
        var uri = CortexIconLibrary.SearchUri("check ✓");
        var page = library.SearchAsync("native", uri, default).GetAwaiter().GetResult();
        Check(page.Items.Count == 1 && host.ByteReads == 0 && host.MetadataReads == 0, "Search hydrates metadata previews only.");
        var preview = page.Items.Single();
        host.Mutate = (_, result) =>
        {
            var data = JsonNode.Parse(result["contents"]![0]!["text"]!.GetValue<string>())!;
            data["items"] = new JsonArray(Enumerable.Range(0, 5).Select(_ => data["items"]![0]!.DeepClone()).ToArray());
            result["contents"]![0]!["text"] = data.ToJsonString();
        };
        Reject(() => library.SearchAsync("native", uri, default).GetAwaiter().GetResult(), "Five results violate requested limit.");
        host.Mutate = null;
        Reject(() => CortexIconLibrary.ValidatePageUri("pegasus-asset://v2/search/knowledge.user?q=x&limit=4"), "Namespace escalation.");
        Reject(() => CortexIconLibrary.ValidatePageUri(uri + "&limit=16"), "Repeated limit.");
        Reject(() => CortexIconLibrary.ValidatePageUri(uri.Replace("&limit=4", "&limit=16")), "Expanded page limit.");
        Reject(() => CortexIconLibrary.SearchUri(new string('x', 257)), "Oversized search.");
        Reject(() => CortexIconLibrary.SearchUri("one two three four five six seven eight nine"), "Nine local search terms.");
        var cursorBoundary = CortexIconLibrary.SearchUri("check") + "&after=" + new string('a', 256);
        CortexIconLibrary.ValidatePageUri(cursorBoundary);
        Check(true, "A 256-character cursor remains admissible.");
        Reject(() => CortexIconLibrary.ValidatePageUri(cursorBoundary + "a"), "257-character cursor.");
        foreach (var malformed in new[]
        {
            "q=%&limit=4", "q=%GG&limit=4", "q=%C3%28&limit=4", "q=a+b&limit=4",
            "q=%0A&limit=4", "q=a=b&limit=4", "q=check&limit=4&after=a&after=b",
            "q=one%20two%20three%20four%20five%20six%20seven%20eight%20nine&limit=4"
        })
            Reject(() => CortexIconLibrary.ValidatePageUri("pegasus-asset://v2/search/knowledge.shared?" + malformed), "Malformed query/cursor.");
        host.Mutate = (_, result) =>
        {
            var data = JsonNode.Parse(result["contents"]![0]!["text"]!.GetValue<string>())!;
            data["nextUri"] = CortexIconLibrary.SearchUri("different terms") + "&after=cursor";
            result["contents"]![0]!["text"] = data.ToJsonString();
        };
        Reject(() => library.SearchAsync("native", uri, default).GetAwaiter().GetResult(), "Next page cannot change selected search terms.");
        host.Mutate = (_, result) => result["contents"]![0]!["uri"] = "cortex://" + new string('b', 64);
        Reject(() => library.MetadataAsync("native", preview, default).GetAwaiter().GetResult(), "Envelope must identify the selected URI.");
        host.Mutate = null;
        var metadata = library.MetadataAsync("native", preview, default).GetAwaiter().GetResult();
        Check(metadata.Preview.Ref == preview.Ref && metadata.License == "TEST-LICENSE" &&
            metadata.Reference.Contains("Meaning used in this deterministic fixture"), "Exact metadata and semantic references.");
        host.Mutate = (_, result) =>
        {
            var data = JsonNode.Parse(result["contents"]![0]!["text"]!.GetValue<string>())!;
            data["id"] = "different-id";
            result["contents"]![0]!["text"] = data.ToJsonString();
        };
        Reject(() => library.MetadataAsync("native", preview, default).GetAwaiter().GetResult(), "Selected exact metadata identity.");
        host.Mutate = null;
        var bytes = library.BytesAsync("native", metadata, default).GetAwaiter().GetResult();
        Check(bytes.SequenceEqual(host.Bytes) && host.ByteReads == 2 &&
            host.ByteRequests.All(request => request.Contains("length=65536", StringComparison.Ordinal)),
            "Two 64 KiB requests produce verified complete content.");
        host.Mutate = (_, result) => result["_meta"]!["ref"] = new string('b', 64);
        Reject(() => library.BytesAsync("native", metadata, default).GetAwaiter().GetResult(), "Byte chunk reference substitution.");
        host.Mutate = (_, result) => result["_meta"]!["sha256"] = new string('0', 64);
        Reject(() => library.BytesAsync("native", metadata, default).GetAwaiter().GetResult(), "Chunk digest corruption.");
        host.Mutate = (_, result) => result["_meta"]!["offset"] = 65536;
        Reject(() => library.BytesAsync("native", metadata, default).GetAwaiter().GetResult(), "Chunk offset substitution.");
        host.Mutate = (_, result) => result["_meta"]!["contentSha256"] = new string('0', 64);
        Reject(() => library.BytesAsync("native", metadata with { Sha256 = new string('0', 64) }, default).GetAwaiter().GetResult(),
            "Passing per-chunk digests cannot bypass the final full digest.");
        host.Mutate = null;
        Reject(() => library.BytesAsync("native", metadata with { Preview = preview with { Size = CortexIconLibrary.MaximumAssetBytes + 1 } }, default)
            .GetAwaiter().GetResult(), "Consumer asset limit before hydration.");
        var requests = host.ByteReads;
        Reject(() => library.BytesAsync("native", metadata, new CancellationToken(true)).GetAwaiter().GetResult(), "Cancelled byte selection.");
        Check(host.ByteReads == requests, "Cancelled operation does not dispatch byte request.");
        using (var selection = new CortexIconSelection())
        {
            var first = selection.Begin(default); var second = selection.Begin(default);
            Check(first.Token.IsCancellationRequested && !selection.IsCurrent(first.Generation) && selection.IsCurrent(second.Generation),
                "New selection invalidates old generation and cancels its token.");
            selection.Dispose();
            Check(second.Token.IsCancellationRequested && !selection.IsCurrent(second.Generation), "Closed picker invalidates current selection.");
        }
        var lateHost = new LateHost();
        using (var cancellation = new CancellationTokenSource())
        {
            var pending = new CortexIconLibrary(lateHost).MetadataAsync("native", preview, cancellation.Token);
            cancellation.Cancel();
            lateHost.Reply.SetResult(host.ExecuteServiceCommandAsync("cortex.assets.read",
                new { serverId = "native", uri = preview.MetadataUri }).GetAwaiter().GetResult());
            Reject(() => pending.GetAwaiter().GetResult(), "A provider ignoring cancellation cannot return stale exact metadata.");
        }
        var scratch = Path.Combine(Path.GetTempPath(), "Cortex-Icon-Consumer-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        try
        {
            var cache = new CortexIconCache(scratch);
            Check(cache.ReadAsync("native", metadata, default).GetAwaiter().GetResult() is null, "First selected cache lookup is absent.");
            cache.SaveAsync("native", metadata, bytes, default).GetAwaiter().GetResult();
            Check(cache.ReadAsync("native", metadata, default).GetAwaiter().GetResult()!.SequenceEqual(bytes), "Content hash cache round trip.");
            Check(cache.ReadAsync("different-provider", metadata, default).GetAwaiter().GetResult() is null, "Cache is provider-bound.");
            Reject(() => cache.ReadAsync("native", metadata with { RawJson = metadata.RawJson + " " }, default).GetAwaiter().GetResult(),
                "Exact metadata binding is required for cached bytes.");
            Reject(() => cache.SaveAsync("native", metadata, new byte[bytes.Length], default).GetAwaiter().GetResult(), "Unverified content cannot enter cache.");
            var content = Directory.GetFiles(Path.Combine(scratch, "cortex-icons-v2"), "content.bin", SearchOption.AllDirectories).Single();
            File.WriteAllBytes(content, new byte[bytes.Length]);
            Reject(() => cache.ReadAsync("native", metadata, default).GetAwaiter().GetResult(), "Cache corruption is rejected.");
            for (var i = 1; i <= 17; ++i)
                cache.SaveAsync("native", metadata with { Preview = preview with { Ref = i.ToString("x64") } }, bytes, default).GetAwaiter().GetResult();
            Check(Directory.GetDirectories(Path.Combine(scratch, "cortex-icons-v2")).Length == CortexIconCache.MaximumEntries,
                "Selected cache retains at most sixteen entries.");
            var totalRoot = Path.Combine(scratch, "total-bound"); Directory.CreateDirectory(totalRoot);
            var totalCache = new CortexIconCache(totalRoot);
            var largeBytes = new byte[CortexIconLibrary.MaximumAssetBytes];
            for (var i = 1; i <= 5; ++i)
                totalCache.SaveAsync("native", metadata with { Preview = preview with { Ref = i.ToString("x64"), Size = largeBytes.Length },
                    Sha256 = CortexIconLibrary.Digest(largeBytes) }, largeBytes, default).GetAwaiter().GetResult();
            Check(Directory.GetFiles(totalRoot, "content.bin", SearchOption.AllDirectories).Sum(path => new FileInfo(path).Length) <=
                CortexIconCache.MaximumTotalBytes, "Cache eviction enforces the eight MiB content ceiling.");
            var output = Path.Combine(scratch, "selected.zip");
            CortexIconContent.Export(output, "native", metadata, bytes);
            using (var archive = ZipFile.OpenRead(output))
            {
                Check(archive.Entries.Count == 2 && archive.GetEntry("asset.svg")!.Length == bytes.Length &&
                    archive.GetEntry("REFERENCE.txt") is not null, "Export includes only selected original bytes and semantic/license references.");
            }
            Reject(() => CortexIconContent.Export(output, "native", metadata, bytes), "Existing export target is preserved.");
            Check(CortexIconContent.Preview(metadata, bytes) is null, "Original SVG verification is not renderer admission.");
            var bitmap = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, new byte[] { 0, 0, 255, 255 }, 4);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var pngStream = new MemoryStream(); encoder.Save(pngStream);
            var png = pngStream.ToArray();
            var pngMetadata = metadata with { Preview = preview with { MimeType = "image/png", Size = png.Length }, Sha256 = CortexIconLibrary.Digest(png) };
            Check(CortexIconContent.Preview(pngMetadata, png) is { IsFrozen: true, PixelWidth: 1, PixelHeight: 1 },
                "Valid bounded PNG is decoded into a frozen bitmap.");
            var jpegEncoder = new JpegBitmapEncoder(); jpegEncoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var jpegStream = new MemoryStream(); jpegEncoder.Save(jpegStream);
            var jpeg = jpegStream.ToArray();
            var jpegMetadata = pngMetadata with { Preview = preview with { MimeType = "image/jpeg", Size = jpeg.Length },
                Sha256 = CortexIconLibrary.Digest(jpeg) };
            Check(CortexIconContent.Preview(jpegMetadata, jpeg) is { IsFrozen: true, PixelWidth: 1, PixelHeight: 1 },
                "Valid bounded JPEG is decoded into a frozen bitmap.");
            var conflicting = png.Take(33).Concat(png.Skip(8).Take(25)).Concat(png.Skip(33)).ToArray();
            Reject(() => CortexIconContent.Preview(pngMetadata with { Sha256 = CortexIconLibrary.Digest(conflicting),
                Preview = pngMetadata.Preview with { Size = conflicting.Length } }, conflicting),
                "A second PNG size header cannot change the decoder canvas.");
            var oversized = (byte[])png.Clone(); BinaryPrimitives.WriteUInt32BigEndian(oversized.AsSpan(16, 4), 50000);
            Reject(() => CortexIconContent.Preview(pngMetadata with { Sha256 = CortexIconLibrary.Digest(oversized) }, oversized),
                "Oversized raster header rejects before decoding.");
            Reject(() => CortexIconContent.Preview(pngMetadata with { Sha256 = metadata.Sha256, Preview = pngMetadata.Preview with { Size = bytes.Length } }, bytes),
                "Media type does not bypass signature validation.");
        }
        finally { Program.RemoveFixtureDirectory(scratch); }
        using (var picker = new CortexIconLibraryPicker(host, null, null, default, _ => Task.CompletedTask))
        {
            // Offscreen fake-provider controls prove the UI admission boundary; no Window or clipboard is involved.
            var controls = LogicalChildren(picker.View).ToArray();
            var export = controls.OfType<Button>().Single(button => button.Content is string text && text.StartsWith("Export", StringComparison.Ordinal));
            Check(!export.IsEnabled && export.Content.ToString()!.Contains("license bundle pending", StringComparison.Ordinal),
                "Selected export begins disabled with the license-bundle boundary visible.");
            picker.ActivateAsync().GetAwaiter().GetResult();
            controls.OfType<ComboBox>().Single().SelectedIndex = 0;
            controls.OfType<TextBox>().Single().Text = "check";
            controls.OfType<Button>().Single(button => Equals(button.Content, "Search")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            controls.OfType<ListBox>().Single().SelectedIndex = 0;
            Check(controls.OfType<TextBlock>().Any(text => text.Text.Contains(preview.MetadataUri, StringComparison.Ordinal)) && !export.IsEnabled,
                "Exact fake selected metadata/content keeps redistribution export disabled.");
            Check(controls.OfType<TextBlock>().Any(text => text.Text.Contains("complete required license and attribution bundle", StringComparison.Ordinal)),
                "The export admission explanation remains readable in the picker.");
        }
        return checks;
    }

    private static IEnumerable<DependencyObject> LogicalChildren(DependencyObject root)
    {
        yield return root;
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
            foreach (var descendant in LogicalChildren(child)) yield return descendant;
    }

    private sealed class LateHost : IDesktopServiceHost
    {
        public TaskCompletionSource<DesktopServiceCommandResult> Reply { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<DesktopServiceCommandResult> ExecuteServiceCommandAsync(string operation, object arguments,
            CancellationToken token = default) => Reply.Task;
    }

    private sealed class ResourceHost : IDesktopServiceHost
    {
        public byte[] Bytes { get; } = Enumerable.Range(0, CortexIconLibrary.ChunkBytes + 11).Select(i => (byte)(i % 251)).ToArray();
        public int ByteReads { get; private set; }
        public int MetadataReads { get; private set; }
        public List<string> ByteRequests { get; } = [];
        public Action<string, JsonObject>? Mutate { get; set; }
        private readonly string reference = new('a', 64);
        public Task<DesktopServiceCommandResult> ExecuteServiceCommandAsync(string operation, object arguments, CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested();
            var args = JsonSerializer.SerializeToElement(arguments);
            JsonObject result;
            if (operation == "cortex.assets.providers")
                result = new() { ["providers"] = new JsonArray(new JsonObject { ["id"] = "native", ["transport"] = "cortex-native-pipe" }), ["totalCount"] = 1 };
            else
            {
                if (operation != "cortex.assets.read" || args.GetProperty("serverId").GetString() != "native")
                    throw new InvalidDataException("Unexpected fake service command.");
                var uri = args.GetProperty("uri").GetString()!;
                if (uri.Contains("?part=bytes", StringComparison.Ordinal))
                {
                    ++ByteReads; ByteRequests.Add(uri);
                    var offset = int.Parse(uri.Split('&').Single(x => x.StartsWith("offset=", StringComparison.Ordinal))[7..]);
                    var chunk = Bytes.Skip(offset).Take(CortexIconLibrary.ChunkBytes).ToArray();
                    result = new()
                    {
                        ["contents"] = new JsonArray(new JsonObject { ["uri"] = uri, ["mimeType"] = "image/svg+xml", ["blob"] = Convert.ToBase64String(chunk) }),
                        ["_meta"] = new JsonObject { ["contract"] = CortexIconLibrary.Contract, ["namespace"] = CortexIconLibrary.Namespace,
                            ["id"] = "test.icon", ["ref"] = reference, ["offset"] = offset, ["length"] = chunk.Length,
                            ["totalSize"] = Bytes.Length, ["sha256"] = CortexIconLibrary.Digest(chunk),
                            ["contentSha256"] = CortexIconLibrary.Digest(Bytes), ["complete"] = offset == 0 && chunk.Length == Bytes.Length }
                    };
                }
                else
                {
                    var data = Item();
                    if (uri.StartsWith("pegasus-asset:", StringComparison.Ordinal))
                    {
                        data["uri"] = "cortex://" + reference;
                        data = new JsonObject { ["contract"] = CortexIconLibrary.Contract, ["namespace"] = CortexIconLibrary.Namespace,
                            ["items"] = new JsonArray(data), ["scanned"] = 1, ["complete"] = true };
                    }
                    else
                    {
                        ++MetadataReads;
                        data["contract"] = CortexIconLibrary.Contract; data["namespace"] = CortexIconLibrary.Namespace;
                        data["sha256"] = CortexIconLibrary.Digest(Bytes); data["metadataUri"] = "cortex://" + reference;
                        data["bytesUri"] = "cortex://" + reference + "?part=bytes";
                        data["license"] = "TEST-LICENSE"; data["source"] = "https://example.invalid/fixture";
                        data["parameters"] = "{\"opaque\":\"never evaluated\"}";
                    }
                    result = new() { ["contents"] = new JsonArray(new JsonObject { ["uri"] = uri, ["mimeType"] = "application/json", ["text"] = data.ToJsonString() }) };
                }
            }
            Mutate?.Invoke(operation, result);
            return Task.FromResult(new DesktopServiceCommandResult(true, JsonSerializer.SerializeToElement(result)));
        }
        private JsonObject Item() => new() { ["id"] = "test.icon", ["ref"] = reference, ["title"] = "Test icon",
            ["meaning"] = "Meaning used in this deterministic fixture", ["mimeType"] = "image/svg+xml", ["size"] = Bytes.Length, ["text"] = "✓" };
    }
}
