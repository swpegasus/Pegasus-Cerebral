using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Windows.Media.Imaging;

namespace Cerebral.ChatIconsPlugin;

public static class CortexIconContent
{
    public const int MaximumDimension = 2048;
    public const long MaximumPixels = 4 * 1024 * 1024;

    /// <summary>Only PNG/JPEG signatures and bounded dimensions enter a native bitmap decoder; SVG stays opaque.</summary>
    public static BitmapSource? Preview(CortexIconMetadata metadata, byte[] bytes)
    {
        Verify(metadata, bytes);
        if (metadata.Preview.MimeType is not ("image/png" or "image/jpeg")) return null;
        var (width, height) = Dimensions(metadata.Preview.MimeType, bytes);
        if (width is < 1 or > MaximumDimension || height is < 1 or > MaximumDimension ||
            (long)width * height > MaximumPixels) throw new InvalidDataException("Raster preview exceeds 2048px or four million pixels.");
        using var stream = new MemoryStream(bytes, writable: false);
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.DecodePixelWidth = Math.Min(width, 256);
        image.StreamSource = stream;
        image.EndInit();
        if (image.PixelWidth > MaximumDimension || image.PixelHeight > MaximumDimension)
            throw new InvalidDataException("Decoded raster exceeds the preview bounds.");
        image.Freeze();
        return image;
    }

    public static (int Width, int Height) Dimensions(string type, byte[] bytes)
    {
        if (type == "image/png")
        {
            if (bytes.Length < 33 || !bytes.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }) ||
                BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(8, 4)) != 13 ||
                !bytes.AsSpan(12, 4).SequenceEqual("IHDR"u8))
                throw new InvalidDataException("PNG signature/header mismatch.");
            var width = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(16, 4));
            var height = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(20, 4));
            if (width > int.MaxValue || height > int.MaxValue) throw new InvalidDataException("PNG dimensions overflow.");
            // Reject conflicting/animated frame headers before the decoder can choose another canvas.
            var position = 8;
            var ended = false;
            while (position + 12 <= bytes.Length)
            {
                var length = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(position, 4));
                if (length > bytes.Length - position - 12) throw new InvalidDataException("PNG chunk exceeds its input.");
                var kind = bytes.AsSpan(position + 4, 4);
                if (kind.SequenceEqual("IHDR"u8) && position != 8 ||
                    kind.SequenceEqual("acTL"u8) || kind.SequenceEqual("fcTL"u8) || kind.SequenceEqual("fdAT"u8))
                    throw new InvalidDataException("Conflicting or animated PNG frame header.");
                if (kind.SequenceEqual("IEND"u8))
                {
                    if (length != 0 || position + 12 != bytes.Length) throw new InvalidDataException("Invalid PNG end marker.");
                    ended = true; break;
                }
                position += 12 + (int)length;
            }
            if (!ended) throw new InvalidDataException("PNG has no bounded end marker.");
            return ((int)width, (int)height);
        }
        if (type != "image/jpeg" || bytes.Length < 4 || bytes[0] != 0xff || bytes[1] != 0xd8)
            throw new InvalidDataException("JPEG signature/header mismatch.");
        var offset = 2;
        (int Width, int Height)? dimensions = null;
        var scan = false;
        while (offset < bytes.Length)
        {
            if (bytes[offset++] != 0xff) throw new InvalidDataException("Invalid JPEG segment.");
            while (offset < bytes.Length && bytes[offset] == 0xff) ++offset;
            if (offset >= bytes.Length) break;
            var marker = bytes[offset++];
            if (marker == 0xda) { scan = true; break; }
            if (marker == 0xd9) break;
            if (marker is 0x01 or >= 0xd0 and <= 0xd7) continue;
            if (offset + 2 > bytes.Length) break;
            var length = BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(offset, 2));
            if (length < 2 || offset + length > bytes.Length) throw new InvalidDataException("Invalid JPEG segment length.");
            if (marker is >= 0xc0 and <= 0xcf && marker is not (0xc4 or 0xc8 or 0xcc))
            {
                if (marker is not (0xc0 or 0xc1 or 0xc2) || dimensions is not null || length < 8)
                    throw new InvalidDataException("Unsupported or conflicting JPEG frame header.");
                dimensions = (BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(offset + 5, 2)),
                    BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(offset + 3, 2)));
            }
            offset += length;
        }
        if (scan && dimensions is { } size) return size;
        throw new InvalidDataException("JPEG has no supported bounded size header.");
    }

    public static void Verify(CortexIconMetadata metadata, byte[] bytes)
    {
        if (bytes.Length is < 1 or > CortexIconLibrary.MaximumAssetBytes ||
            bytes.Length != metadata.Preview.Size || CortexIconLibrary.Digest(bytes) != metadata.Sha256)
            throw new InvalidDataException("Selected original bytes do not match their exact metadata.");
    }

    /// <summary>An explicit export contains only the selected original bytes and readable license/provenance references.</summary>
    public static void Export(string destination, string provider, CortexIconMetadata metadata, byte[] bytes)
    {
        Verify(metadata, bytes);
        if (!Path.IsPathFullyQualified(destination)) throw new ArgumentException("An exact export path is required.");
        var target = Path.GetFullPath(destination);
        CortexIconCache.RejectLinks(target);
        if (File.Exists(target) || Directory.Exists(target)) throw new IOException("Choose a new export file; existing files are preserved.");
        var parent = Path.GetDirectoryName(target)!;
        if (!Directory.Exists(parent)) throw new DirectoryNotFoundException("The selected export directory is unavailable.");
        var stage = Path.Combine(parent, ".cortex-icon-export-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var stream = new FileStream(stage, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
            {
                var extension = metadata.Preview.MimeType switch
                { "image/svg+xml" => ".svg", "image/png" => ".png", "image/jpeg" => ".jpg", _ => ".bin" };
                using (var asset = archive.CreateEntry("asset" + extension).Open()) asset.Write(bytes);
                using var reference = new StreamWriter(archive.CreateEntry("REFERENCE.txt").Open(), new UTF8Encoding(false));
                reference.Write("Selected original content; renderer admission is separate.\nprovider=" + provider + "\n" + metadata.Reference +
                    "\nLicense/provenance references accompany this export. Inspect the referenced terms before reuse.\n");
            }
            CortexIconCache.RejectLinks(target);
            File.Move(stage, target);
        }
        finally { if (File.Exists(stage)) File.Delete(stage); }
    }
}
