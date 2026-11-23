using System.Buffers;
using System.Runtime.CompilerServices;
using System.Text;

namespace Cerebral.HostedProviderProtocol;

/// <summary>A framed event only. JSON, provider completion and retry policy belong to its consumer.</summary>
internal sealed record SseFrame(string EventName, string Data);

internal sealed record SseReaderLimits(
    int MaximumLineUtf8Bytes = 65_536,
    int MaximumEventUtf8Bytes = 262_144,
    long MaximumStreamBytes = 16_777_216)
{
    internal void Validate()
    {
        if (MaximumLineUtf8Bytes is < 1 or > 262_144 ||
            MaximumEventUtf8Bytes < MaximumLineUtf8Bytes || MaximumEventUtf8Bytes > 1_048_576 ||
            MaximumStreamBytes < MaximumEventUtf8Bytes || MaximumStreamBytes > 67_108_864)
            throw new ArgumentOutOfRangeException(nameof(SseReaderLimits), "SSE limits must be positive, ordered and bounded.");
    }
}

/// <summary>Strict UTF-8, bounded SSE framing without HTTP, reconnection or ownership of the input stream.</summary>
internal static class BoundedSseReader
{
    public static async IAsyncEnumerable<SseFrame> ReadAsync(Stream stream, SseReaderLimits? limits = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanRead) throw new ArgumentException("A readable SSE stream is required.", nameof(stream));
        limits ??= new();
        limits.Validate();
        cancellationToken.ThrowIfCancellationRequested();
        var processor = new FrameProcessor(limits);
        var line = new byte[limits.MaximumLineUtf8Bytes];
        var lineLength = 0;
        var skipLineFeed = false;
        long receivedBytes = 0;
        var readBuffer = ArrayPool<byte>.Shared.Rent(4096);
        try
        {
            while (true)
            {
                var read = await stream.ReadAsync(readBuffer.AsMemory(0, 4096), cancellationToken).ConfigureAwait(false);
                if (read == 0) break;
                if (read < 0 || read > 4096) throw new InvalidDataException("The SSE stream returned an invalid read length.");
                receivedBytes = checked(receivedBytes + read);
                if (receivedBytes > limits.MaximumStreamBytes)
                    throw new InvalidDataException("The SSE stream exceeds its byte bound.");
                for (var index = 0; index < read; index++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var value = readBuffer[index];
                    if (skipLineFeed)
                    {
                        skipLineFeed = false;
                        if (value == (byte)'\n') continue;
                    }
                    if (value is (byte)'\r' or (byte)'\n')
                    {
                        var frame = processor.Process(line, lineLength);
                        lineLength = 0;
                        skipLineFeed = value == (byte)'\r';
                        if (frame is not null) yield return frame;
                    }
                    else
                    {
                        if (lineLength == line.Length)
                            throw new InvalidDataException("An SSE line exceeds its byte bound.");
                        line[lineLength++] = value;
                    }
                }
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (lineLength != 0) processor.ValidateIncompleteLine(line, lineLength);
            // SSE dispatch requires a blank line. An unfinished line or event at EOF is discarded;
            // the provider consumer decides whether its terminal was missing or incomplete.
        }
        finally
        {
            Array.Clear(line);
            ArrayPool<byte>.Shared.Return(readBuffer, clearArray: true);
        }
    }

    private sealed class FrameProcessor(SseReaderLimits limits)
    {
        private static readonly UTF8Encoding Utf8 = new(false, true);
        private readonly StringBuilder data = new();
        private string eventName = "";
        private long eventBytes;
        private bool firstLine = true;

        internal void ValidateIncompleteLine(byte[] bytes, int length)
        {
            _ = Decode(bytes, length);
            if (eventBytes + length > limits.MaximumEventUtf8Bytes)
                throw new InvalidDataException("An SSE event exceeds its byte bound.");
        }

        private static string Decode(byte[] bytes, int length)
        {
            try { return Utf8.GetString(bytes, 0, length); }
            catch (DecoderFallbackException)
            {
                // A lossy replacement could turn malformed provider JSON into a different value.
                throw new InvalidDataException("The SSE stream contains invalid UTF-8.");
            }
        }

        internal SseFrame? Process(byte[] bytes, int length)
        {
            var line = Decode(bytes, length);
            if (firstLine)
            {
                firstLine = false;
                if (line.StartsWith('\uFEFF')) line = line[1..];
            }
            if (line.Length == 0)
            {
                SseFrame? frame = data.Length == 0 ? null :
                    new(eventName.Length == 0 ? "message" : eventName, data.ToString(0, data.Length - 1));
                eventName = "";
                data.Clear();
                eventBytes = 0;
                return frame;
            }
            // Include ignored comments/fields so a huge unfinished event cannot bypass its bound.
            eventBytes = checked(eventBytes + length + 1L);
            if (eventBytes > limits.MaximumEventUtf8Bytes)
                throw new InvalidDataException("An SSE event exceeds its byte bound.");
            if (line[0] == ':') return null;
            var colon = line.IndexOf(':');
            var field = colon < 0 ? line : line[..colon];
            var value = colon < 0 ? "" : line[(colon + 1)..];
            if (value.StartsWith(' ')) value = value[1..]; // Strip exactly one space, as SSE specifies.
            if (field == "event") eventName = value;
            else if (field == "data") data.Append(value).Append('\n');
            // id/retry and unknown fields do not initiate reconnection or alter transport policy.
            return null;
        }
    }
}
