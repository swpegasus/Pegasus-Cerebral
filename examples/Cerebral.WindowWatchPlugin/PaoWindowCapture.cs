using System.Buffers.Binary;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Cerebral.WindowWatchPlugin;

internal sealed record WindowSnapshot(BitmapSource Bitmap, string PixelHash)
{
    internal static WindowSnapshot Decode(string path, long expectedBytes, string expectedSha, int maximumSide)
    {
        using var input = File.OpenRead(path);
        if (input.Length is < 24 or > 32 * 1024 * 1024)
            throw new InvalidDataException("The captured image is empty or exceeds the size limit.");

        if (input.Length != expectedBytes ||
            !Convert.ToHexString(SHA256.HashData(input)).Equals(expectedSha, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The captured file did not match the Cortex receipt.");
        input.Position = 0;
        Span<byte> header = stackalloc byte[24];
        input.ReadExactly(header);
        if (!header[..8].SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }) ||
            !header[12..16].SequenceEqual("IHDR"u8))
            throw new InvalidDataException("Cortex did not return a PNG capture.");
        var width = BinaryPrimitives.ReadInt32BigEndian(header[16..20]);
        var height = BinaryPrimitives.ReadInt32BigEndian(header[20..24]);
        if (width is < 1 or > 4096 || height is < 1 or > 4096)
            throw new InvalidDataException("The window capture exceeds 4096 pixels per side.");
        input.Position = 0;

        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.StreamSource = input;
        if (width >= height && width > maximumSide) bitmap.DecodePixelWidth = maximumSide;
        if (height > width && height > maximumSide) bitmap.DecodePixelHeight = maximumSide;
        bitmap.EndInit();

        var normalized = new FormatConvertedBitmap(bitmap, PixelFormats.Bgr24, null, 0);
        normalized.Freeze();
        var stride = normalized.PixelWidth * 3;
        var pixels = new byte[stride * normalized.PixelHeight];
        try
        {
            normalized.CopyPixels(pixels, stride, 0);
            if (pixels.AsSpan().IndexOfAnyExcept((byte)0) < 0)
                throw new InvalidDataException("The selected window returned a blank capture. Restore the window or select another.");
            var hash = Convert.ToHexString(SHA256.HashData(pixels));
            return new(normalized, $"{normalized.PixelWidth}x{normalized.PixelHeight}:{hash}");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(pixels);
        }
    }

    internal byte[] Encode(bool textDetail)
    {
        BitmapEncoder encoder = textDetail
            ? new PngBitmapEncoder()
            : new JpegBitmapEncoder { QualityLevel = 88 };
        encoder.Frames.Add(BitmapFrame.Create(Bitmap));
        using var output = new MemoryStream();
        encoder.Save(output);
        var maximumBytes = (textDetail ? 6 : 2) * 1024 * 1024;
        if (output.Length > maximumBytes)
            throw new InvalidDataException("The snapshot is too large. Turn off Text detail or resize the selected window.");
        return output.ToArray();
    }
}

internal static class PaoWindowCapture
{
    internal static async Task<WindowSnapshot> CaptureAsync(WindowTarget target, bool textDetail)
    {
        var pao = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "Pegasus", "Cortex", "bin", "pao.exe");
        if (!File.Exists(pao))
            throw new InvalidOperationException("Cortex window capture is unavailable. Start the installed Cortex tray.");

        var scratch = Directory.CreateTempSubdirectory("CerebralWindowWatch-");
        var png = Path.Combine(scratch.FullName, "snapshot.png");
        try
        {
            var start = new ProcessStartInfo(pao)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = scratch.FullName
            };
            foreach (var arg in new[]
            {
                "--json", "ui", "capture", $"0x{target.Handle.ToInt64():x}",
                target.ProcessId.ToString(CultureInfo.InvariantCulture),
                target.ProcessStartUtc.ToString("O", CultureInfo.InvariantCulture), png, "tmo=5"
            })
                start.ArgumentList.Add(arg);

            using var process = Process.Start(start)
                ?? throw new InvalidOperationException("Cortex capture could not start.");
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var output = process.StandardOutput.ReadToEndAsync(deadline.Token);
            var stderr = process.StandardError.BaseStream.CopyToAsync(Stream.Null, deadline.Token);
            try
            {
                await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
                await Task.WhenAll(output, stderr).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (deadline.IsCancellationRequested)
            {
                // The Cortex job has its own shorter deadline. This is the exact child client
                // started here, and the unique destination is retired before any later capture.
                try { if (!process.HasExited) process.Kill(); }
                catch (Exception error) when (error is InvalidOperationException or Win32Exception) { }
                try { await Task.WhenAll(output, stderr).ConfigureAwait(false); }
                catch (OperationCanceledException) { }
                throw new InvalidOperationException("Cortex capture timed out. Check the selected window and the Cortex tray.");
            }
            using var receipt = ParseReceipt(await output.ConfigureAwait(false));
            var body = receipt.RootElement;
            if (process.ExitCode != 0 || !body.TryGetProperty("ok", out var ok) || ok.ValueKind != JsonValueKind.True)
                throw new InvalidOperationException(CaptureError(body, process.ExitCode));
            if (!body.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object ||
                !data.TryGetProperty("dst", out var destination) || destination.ValueKind != JsonValueKind.String ||
                !string.Equals(destination.GetString(), png, StringComparison.OrdinalIgnoreCase) ||
                !data.TryGetProperty("sha", out var hash) || hash.ValueKind != JsonValueKind.String ||
                hash.GetString() is not { Length: 64 } expectedHash ||
                !data.TryGetProperty("bytes", out var bytes) || !bytes.TryGetInt64(out var length) ||
                !File.Exists(png))
                throw new InvalidDataException("Cortex did not confirm the selected window capture.");
            return WindowSnapshot.Decode(png, length, expectedHash, textDetail ? 2560 : 1600);
        }
        finally
        {
            // Cortex publishes via a temporary sibling. Clean only files belonging to this
            // capture in the new private directory, and surface a cleanup failure to the user.
            try
            {
                File.Delete(png);
                foreach (var temporary in scratch.EnumerateFiles(".snapshot.png.pao-*.tmp"))
                    temporary.Delete();
                scratch.Delete();
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                throw new InvalidOperationException($"Snapshot cleanup needs attention in {scratch.FullName}.", error);
            }
        }
    }

    private static JsonDocument ParseReceipt(string output)
    {
        if (output.Length is < 2 or > 16384)
            throw new InvalidDataException("Cortex did not return a window capture receipt.");
        try
        {
            var receipt = JsonDocument.Parse(output);
            if (receipt.RootElement.ValueKind == JsonValueKind.Object) return receipt;
            receipt.Dispose();
        }
        catch (JsonException) { }
        throw new InvalidDataException("Cortex returned an unreadable window capture receipt.");
    }

    private static string CaptureError(JsonElement receipt, int exitCode)
    {
        var code = receipt.TryGetProperty("err", out var error) && error.ValueKind == JsonValueKind.String
            ? error.GetString()
            : null;
        if (code == "stale_target")
            return "The selected window is no longer available. Refresh the list and select it again.";
        if (code == "ui_unsupported")
            return "Cortex could not capture this window. Restore it or select another window.";
        var detail = code is not null && Regex.IsMatch(code, "^[a-z0-9_]{1,40}$") ? code : $"exit {exitCode}";
        return $"Cortex capture failed ({detail}). Check the Cortex tray and the selected window.";
    }
}
