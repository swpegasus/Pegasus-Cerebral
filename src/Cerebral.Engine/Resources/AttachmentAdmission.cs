using System.Buffers.Binary;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;

namespace Cerebral.Engine.Resources;

/// <summary>
/// Bounded file admission, not a decoder or proof that a file is safe for an arbitrary downstream parser.
/// Rendering/model adapters must still apply their decoder and pixel limits to the resolved original.
/// </summary>
public static class AttachmentAdmission
{
    internal const int MaximumImageDimension = 16_384;
    internal const long MaximumImagePixels = 40_000_000;
    internal static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly IReadOnlyDictionary<string, string> TextTypes =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [".txt"] = "text/plain", [".md"] = "text/markdown", [".markdown"] = "text/markdown",
            [".csv"] = "text/csv", [".tsv"] = "text/tab-separated-values", [".log"] = "text/plain",
            [".json"] = "application/json", [".xml"] = "application/xml",
            [".yaml"] = "text/plain", [".yml"] = "text/plain",
            [".cs"] = "text/plain", [".py"] = "text/plain", [".js"] = "text/plain",
            [".ts"] = "text/plain", [".css"] = "text/plain", [".sql"] = "text/plain"
        };
    private static readonly IReadOnlyDictionary<string, string> ArchiveTypes =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [".zip"] = "application/zip", [".7z"] = "application/x-7z-compressed",
            [".rar"] = "application/vnd.rar", [".tar"] = "application/x-tar",
            [".gz"] = "application/gzip", [".tgz"] = "application/gzip",
            [".bz2"] = "application/x-bzip2", [".xz"] = "application/x-xz",
            [".docx"] = "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            [".xlsx"] = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            [".pptx"] = "application/vnd.openxmlformats-officedocument.presentationml.presentation",
            [".jar"] = "application/java-archive"
        };

    internal sealed record Inspection(string MediaType, IReadOnlyDictionary<string, string>? Metadata);
    public readonly record struct ImageInspection(string MediaType, int Width, int Height);

    /// <summary>Reinspect exact image bytes at a model or transport boundary. This validates
    /// framing and dimensions only; the downstream codec still owns complete decoding.</summary>
    public static ImageInspection InspectImage(string mediaType, ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length > AttachmentStore.MaximumAttachmentBytes)
            throw new InvalidDataException($"Image attachment exceeds {AttachmentStore.MaximumAttachmentBytes} bytes.");
        var dimensions = mediaType switch
        {
            "image/png" => Png(bytes),
            "image/jpeg" => Jpeg(bytes),
            _ => throw new NotSupportedException("Native image input requires an admitted PNG or JPEG.")
        };
        return new(mediaType, dimensions.Width, dimensions.Height);
    }

    internal static int MaximumBytes(string originalName) =>
        TextTypes.ContainsKey(Path.GetExtension(originalName)) ? AttachmentStore.MaximumTextBytes :
        AttachmentStore.MaximumAttachmentBytes;

    internal static Inspection Inspect(string originalName, ReadOnlySpan<byte> bytes)
    {
        var extension = Path.GetExtension(originalName);
        if (TextTypes.TryGetValue(extension, out var mediaType))
        {
            if (bytes.Length > AttachmentStore.MaximumTextBytes)
                throw new InvalidDataException($"Text attachment exceeds {AttachmentStore.MaximumTextBytes} UTF-8 bytes.");
            try { _ = StrictUtf8.GetCharCount(bytes); }
            catch (DecoderFallbackException ex) { throw new InvalidDataException("Text attachment is not valid UTF-8. Convert its encoding before importing.", ex); }
            if (bytes.Contains((byte)0)) throw new InvalidDataException("Text attachment contains NUL bytes and is not admitted as text.");
            return new(mediaType, null);
        }
        if (extension.Equals(".png", StringComparison.OrdinalIgnoreCase))
        {
            var image = InspectImage("image/png", bytes);
            return new(image.MediaType, Dimensions(image.Width, image.Height));
        }
        if (IsImageExtension(extension))
        {
            var image = InspectImage("image/jpeg", bytes);
            return new(image.MediaType, Dimensions(image.Width, image.Height));
        }
        var metadata = new ReadOnlyDictionary<string, string>(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["file.extension"] = extension.ToLowerInvariant()
        });
        if (ArchiveTypes.TryGetValue(extension, out var archiveType))
        {
            var archiveMetadata = new Dictionary<string, string>(metadata, StringComparer.Ordinal)
            {
                ["handler.archive"] = "cortex.7zip"
            };
            return new(archiveType, new ReadOnlyDictionary<string, string>(archiveMetadata));
        }
        return new("application/octet-stream", metadata);
    }

    internal static bool IsText(string mediaType) =>
        TextTypes.Values.Contains(mediaType, StringComparer.Ordinal);

    internal static bool IsArchive(string mediaType) =>
        ArchiveTypes.Values.Contains(mediaType, StringComparer.Ordinal);

    private static bool IsImageExtension(string extension) =>
        extension.Equals(".png", StringComparison.OrdinalIgnoreCase) ||
        extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase) ||
        extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase) ||
        extension.Equals(".jpe", StringComparison.OrdinalIgnoreCase);

    private static IReadOnlyDictionary<string, string> Dimensions(int width, int height)
    {
        CheckDimensions(width, height);
        return new ReadOnlyDictionary<string, string>(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["image.width"] = width.ToString(CultureInfo.InvariantCulture),
            ["image.height"] = height.ToString(CultureInfo.InvariantCulture)
        });
    }

    private static void CheckDimensions(int width, int height)
    {
        if (width <= 0 || height <= 0 || width > MaximumImageDimension || height > MaximumImageDimension ||
            (long)width * height > MaximumImagePixels)
            throw new InvalidDataException($"Image dimensions exceed admission limits ({MaximumImageDimension} per side, {MaximumImagePixels} pixels).");
    }

    private static (int Width, int Height) Png(ReadOnlySpan<byte> bytes)
    {
        ReadOnlySpan<byte> signature = [137, 80, 78, 71, 13, 10, 26, 10];
        if (bytes.Length < 45 || !bytes[..8].SequenceEqual(signature)) throw InvalidImage("PNG");
        var offset = 8; var width = 0; var height = 0; var data = false; var first = true;
        while (offset <= bytes.Length - 12)
        {
            var rawLength = BinaryPrimitives.ReadUInt32BigEndian(bytes.Slice(offset, 4));
            if (rawLength > bytes.Length - offset - 12) throw InvalidImage("PNG");
            var length = (int)rawLength;
            var type = bytes.Slice(offset + 4, 4);
            if (first)
            {
                if (!type.SequenceEqual("IHDR"u8) || length != 13) throw InvalidImage("PNG");
                var rawWidth = BinaryPrimitives.ReadUInt32BigEndian(bytes.Slice(offset + 8, 4));
                var rawHeight = BinaryPrimitives.ReadUInt32BigEndian(bytes.Slice(offset + 12, 4));
                if (rawWidth > int.MaxValue || rawHeight > int.MaxValue) throw InvalidImage("PNG");
                width = (int)rawWidth; height = (int)rawHeight; CheckDimensions(width, height);
                var depth = bytes[offset + 16]; var color = bytes[offset + 17];
                var validDepth = color switch
                {
                    0 => depth is 1 or 2 or 4 or 8 or 16,
                    2 or 4 or 6 => depth is 8 or 16,
                    3 => depth is 1 or 2 or 4 or 8,
                    _ => false
                };
                if (!validDepth || bytes[offset + 18] != 0 || bytes[offset + 19] != 0 || bytes[offset + 20] > 1)
                    throw InvalidImage("PNG");
                first = false;
            }
            else if (type.SequenceEqual("IHDR"u8)) throw InvalidImage("PNG");
            if (type.SequenceEqual("acTL"u8)) throw new NotSupportedException("Animated PNG is not supported by this attachment route.");
            if (type.SequenceEqual("IDAT"u8)) data |= length > 0;
            if (type.SequenceEqual("IEND"u8))
            {
                if (length != 0 || !data || offset + 12 != bytes.Length) throw InvalidImage("PNG");
                return (width, height);
            }
            // Chunk lengths and required framing are checked. CRC/inflate validation belongs to the decoder.
            offset += length + 12;
        }
        throw InvalidImage("PNG");
    }

    private static (int Width, int Height) Jpeg(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 20 || bytes[0] != 0xff || bytes[1] != 0xd8 ||
            bytes[^2] != 0xff || bytes[^1] != 0xd9) throw InvalidImage("JPEG");
        var offset = 2; var width = 0; var height = 0;
        while (offset < bytes.Length - 2)
        {
            if (bytes[offset++] != 0xff) throw InvalidImage("JPEG");
            while (offset < bytes.Length && bytes[offset] == 0xff) offset++;
            if (offset >= bytes.Length) throw InvalidImage("JPEG");
            var marker = bytes[offset++];
            if (marker is 0 or 0xd8 or 0xd9 or >= 0xd0 and <= 0xd7) throw InvalidImage("JPEG");
            if (offset > bytes.Length - 2) throw InvalidImage("JPEG");
            var length = BinaryPrimitives.ReadUInt16BigEndian(bytes.Slice(offset, 2));
            if (length < 2 || length > bytes.Length - offset) throw InvalidImage("JPEG");
            if (marker is 0xc0 or 0xc1 or 0xc2)
            {
                if (width != 0 || length < 11 || bytes[offset + 2] != 8) throw InvalidImage("JPEG");
                height = BinaryPrimitives.ReadUInt16BigEndian(bytes.Slice(offset + 3, 2));
                width = BinaryPrimitives.ReadUInt16BigEndian(bytes.Slice(offset + 5, 2));
                var components = bytes[offset + 7];
                if (components is not (1 or 3 or 4) || length != 8 + 3 * components) throw InvalidImage("JPEG");
                CheckDimensions(width, height);
            }
            if (marker == 0xda)
            {
                if (width == 0 || length < 6 || offset + length >= bytes.Length - 2) throw InvalidImage("JPEG");
                return (width, height);
            }
            offset += length;
        }
        throw InvalidImage("JPEG");
    }

    private static InvalidDataException InvalidImage(string kind) =>
        new($"Attachment is not an admitted {kind} image: its signature, dimensions, or structural framing is invalid.");
}
