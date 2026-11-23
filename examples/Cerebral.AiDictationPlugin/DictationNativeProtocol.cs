using System.Buffers.Binary;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Cerebral.AiDictationPlugin;

// Private, versioned child-process protocol. No command, target/window identity,
// arbitrary file operation, credentials or desktop authority crosses this pipe.
internal sealed record DictationNativeRequest(int Version, long Id, string Operation, byte[]? Pcm = null);
internal sealed record DictationNativeResult(string Text, bool Final);
internal sealed record DictationNativeResponse(int Version, long Id, string State, string? Message = null,
    DictationNativeResult[]? Results = null, string? SdkVersion = null);
internal static class DictationNativeProtocol
{
    internal const int Version = 1, MaximumFrameBytes = 262144, MaximumPcmBytes = 6400, MaximumTextCharacters = 32768;
    private static readonly JsonSerializerOptions Json = new() { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
    internal static async Task WriteAsync<T>(Stream output, T value, CancellationToken token)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, Json);
        if (bytes.Length is < 1 or > MaximumFrameBytes) throw new InvalidDataException("The private speech message exceeds its bound.");
        var header = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(header, bytes.Length);
        await output.WriteAsync(header, token); await output.WriteAsync(bytes, token); await output.FlushAsync(token);
    }
    internal static async Task<T> ReadAsync<T>(Stream input, CancellationToken token)
    {
        var header = new byte[4]; await input.ReadExactlyAsync(header, token);
        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length is < 1 or > MaximumFrameBytes) throw new InvalidDataException("The private speech frame is invalid or too large.");
        var body = new byte[length]; await input.ReadExactlyAsync(body, token);
        return JsonSerializer.Deserialize<T>(body, Json) ?? throw new InvalidDataException("The private speech message is empty.");
    }
    internal static void CheckPcm(byte[]? pcm)
    {
        if (pcm is null || pcm.Length is < 2 or > MaximumPcmBytes || pcm.Length % 2 != 0)
            throw new InvalidDataException("Speech input must be a bounded complete mono16k PCM16 chunk.");
    }
}
