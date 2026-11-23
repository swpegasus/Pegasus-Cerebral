using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Cerebral.Core;

/// <summary>Nonsecret, machine-local binding. Enrollment and the protected key are owned by Cortex.</summary>
public sealed record CortexNativeConnection(string HostExecutable, string CredentialFile, int SessionId,
    CortexNativeRelay? Relay = null, string? PipeName = null)
{
    public void Validate()
    {
        PathField(HostExecutable); PathField(CredentialFile);
        if (SessionId < 0) throw new ArgumentException("Cortex native binding requires the observed Windows session.");
        Relay?.Validate();
        if (PipeName is not null && (PipeName.Length is < 1 or > 160 || PipeName.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('.' or '-' or '_'))))
            throw new ArgumentException("Cortex native endpoint must be an explicit local pipe name.");
    }
    internal static void PathField(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 4096 || value.Any(char.IsControl) || !Path.IsPathFullyQualified(value))
            throw new ArgumentException("Native Cortex paths must be explicit bounded absolute paths on this computer.");
    }
}

public sealed record CortexNativeRelay(string PipeName, string OwnerSid, int SessionId, string Executable)
{
    public void Validate()
    {
        if (string.IsNullOrEmpty(PipeName) || PipeName.Length > 160 || PipeName.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('.' or '-' or '_')) ||
            string.IsNullOrWhiteSpace(OwnerSid) || OwnerSid.Length > 184 || !OwnerSid.StartsWith("S-1-", StringComparison.Ordinal) || SessionId < 0)
            throw new ArgumentException("Cortex relay requires its exact local pipe, owner SID and Windows session.");
        CortexNativeConnection.PathField(Executable);
    }
}

public sealed record CortexNativeIdentity(string Contract, string Product, string Version, string OwnerSid,
    int Session, string ClientId, string State, string StorageKind, int MaximumRequestBytes,
    int MaximumResponseBytes, int MaximumConnections, bool Network);

/// <summary>Cortex provider's native v1 framing; no JSON-RPC or MCP lifecycle is carried on this route.</summary>
public static class CortexNativeProtocol
{
    public const string Contract = "pegasus.cortex.native/1";
    public const int HelloBytes = 4096;
    public const int RequestBytes = 262144;
    public const int ResponseBytes = 524288;
    // The local relay adds only a bounded request identity/result envelope to a native reply.
    public const int RelayResponseBytes = ResponseBytes + 256;
    public static string PipeName(int session) => session >= 0 ? "pegasus-cortex-native-v1-" + session.ToString(System.Globalization.CultureInfo.InvariantCulture)
        : throw new ArgumentOutOfRangeException(nameof(session));
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { MaxDepth = 64 };

    // Finish local serialization and size validation before dispatch starts. The callback marks
    // the point after which a disconnect cannot prove that a mutation did not run.
    public static async Task WriteAsync<T>(Stream stream, T value, int maximum, CancellationToken token, Action? beforeWrite = null)
    {
        if (maximum is < 1 or > RelayResponseBytes) throw new ArgumentOutOfRangeException(nameof(maximum));
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, Json);
        try
        {
            if (bytes.Length < 1 || bytes.Length > maximum) throw new InvalidDataException("Cortex native request exceeds its frame limit. No request was sent.");
            token.ThrowIfCancellationRequested();
            var header = new byte[8];
            BinaryPrimitives.WriteUInt16LittleEndian(header, 1);
            BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(2), 0);
            BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(4), bytes.Length);
            beforeWrite?.Invoke();
            await stream.WriteAsync(header, token);
            await stream.WriteAsync(bytes, token);
            await stream.FlushAsync(token);
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    public static async Task<JsonElement> ReadAsync(Stream stream, int maximum, CancellationToken token)
    {
        if (maximum is < 1 or > RelayResponseBytes) throw new ArgumentOutOfRangeException(nameof(maximum));
        var header = new byte[8]; await stream.ReadExactlyAsync(header, token);
        var length = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(4));
        if (BinaryPrimitives.ReadUInt16LittleEndian(header) != 1 || BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(2)) > 0 || length is < 1 || length > maximum)
            throw new InvalidDataException("Cortex native frame version or length is incompatible.");
        var bytes = new byte[length];
        try
        {
            await stream.ReadExactlyAsync(bytes, token);
            _ = Utf8.GetCharCount(bytes);
            using var document = JsonDocument.Parse(bytes, new() { MaxDepth = 64 });
            RejectDuplicates(document.RootElement);
            return document.RootElement.Clone();
        }
        catch (Exception error) when (error is JsonException or DecoderFallbackException)
        { throw new InvalidDataException("Cortex native frame contains invalid UTF-8 JSON."); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    internal static void RejectDuplicates(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new InvalidDataException("Cortex native frame contains duplicate properties.");
                RejectDuplicates(property.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array) foreach (var item in value.EnumerateArray()) RejectDuplicates(item);
    }
}
