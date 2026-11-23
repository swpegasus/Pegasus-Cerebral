using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Cerebral.Core;

/// <summary>One local Worker generation. A restarted Worker must advertise a new instance identity.</summary>
public sealed record LocalWorkerEndpoint(string PipeName, string InstanceId)
{
    public void Validate()
    {
        if (string.IsNullOrEmpty(PipeName) || PipeName.Length > 160 || PipeName.Any(character =>
            !char.IsAsciiLetterOrDigit(character) && character is not ('.' or '-' or '_')))
            throw new ArgumentException("The local Worker pipe name must contain 1–160 ASCII letters, digits, dots, dashes or underscores.", nameof(PipeName));
        LocalWorkerProtocol.ValidateInstanceId(InstanceId);
    }
}

public sealed record LocalWorkerHello(string Product, int Protocol, string InstanceId);
public sealed record LocalWorkerRequest(string Kind, CommandRequest? Command = null);

/// <summary>Version-one framing for the same-user local Worker transport. A frame is a four-byte
/// little-endian payload length followed by strict UTF-8 JSON. Framing is not authorization.</summary>
public static class LocalWorkerProtocol
{
    public const string Product = "Pegasus Cerebral";
    public const int Version = 1;
    public const int MaximumFrameBytes = 4 * 1024 * 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly JsonSerializerOptions Json = new(CerebralJson.Options)
    {
        WriteIndented = false,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        NumberHandling = JsonNumberHandling.Strict,
        RespectRequiredConstructorParameters = true,
        RespectNullableAnnotations = true,
        MaxDepth = 32
    };

    public static LocalWorkerHello CreateHello(LocalWorkerEndpoint endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        endpoint.Validate();
        return new(Product, Version, endpoint.InstanceId);
    }

    public static void ValidateHello(LocalWorkerHello hello, LocalWorkerEndpoint endpoint)
    {
        ArgumentNullException.ThrowIfNull(hello);
        ArgumentNullException.ThrowIfNull(endpoint);
        endpoint.Validate();
        ValidateMessage(hello);
        if (!string.Equals(hello.Product, Product, StringComparison.Ordinal))
            throw new InvalidDataException("The local pipe does not identify itself as Pegasus Cerebral. No command was sent.");
        if (hello.Protocol != Version)
            throw new InvalidDataException("The local Worker protocol is incompatible. Refresh the local connection before sending commands.");
        if (!string.Equals(hello.InstanceId, endpoint.InstanceId, StringComparison.Ordinal))
            throw new InvalidDataException("The local Worker instance changed or does not match its discovery record. Refresh discovery; commands are not replayed.");
    }

    public static void ValidateRequest(LocalWorkerRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        switch (request.Kind)
        {
            case "command":
                if (request.Command is null) throw new ArgumentException("A local command request requires its command.", nameof(request));
                ValidateCommand(request.Command);
                break;
            case "events":
                if (request.Command is not null) throw new ArgumentException("An event subscription cannot include a command.", nameof(request));
                break;
            default:
                throw new ArgumentException("The local Worker request kind must be 'command' or 'events'.", nameof(request));
        }
    }

    public static async Task<T> ReadAsync<T>(Stream stream, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var header = new byte[sizeof(int)];
        await ReadExactlyAsync(stream, header, "frame header", cancellationToken).ConfigureAwait(false);
        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length is <= 0 or > MaximumFrameBytes)
            throw new InvalidDataException("The local Worker frame length must be between 1 byte and 4 MiB.");
        var payload = new byte[length];
        await ReadExactlyAsync(stream, payload, "frame payload", cancellationToken).ConfigureAwait(false);
        try
        {
            ValidateJson(payload);
            var result = JsonSerializer.Deserialize<T>(payload, Json);
            if (result is null) throw new InvalidDataException("The local Worker returned a null protocol message.");
            ValidateMessage(result);
            return result;
        }
        catch (Exception error) when (error is JsonException or ArgumentException)
        {
            throw new InvalidDataException("The local Worker frame is not a valid strict UTF-8 protocol message. Check its version and required fields.", error);
        }
    }

    /// <summary>Validation and serialization finish synchronously before any bytes are written. A caller
    /// may therefore distinguish invalid local input from an uncertain failure after dispatch begins.</summary>
    public static Task WriteAsync<T>(Stream stream, T value, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(value);
        cancellationToken.ThrowIfCancellationRequested();
        ValidateMessage(value);
        byte[] payload;
        using (var buffer = new BoundedBuffer())
        {
            JsonSerializer.Serialize(buffer, value, Json);
            payload = buffer.ToArray();
        }
        ValidateJson(payload);
        return WriteFrameAsync(stream, payload, cancellationToken);
    }

    internal static void ValidateInstanceId(string instanceId)
    {
        if (instanceId is null || !Guid.TryParseExact(instanceId, "N", out var instance) || instance == Guid.Empty)
            throw new ArgumentException("The local Worker instance must be a nonempty GUID in N format.", nameof(instanceId));
    }

    private static void ValidateCommand(CommandRequest command)
    {
        Identifier(command.Operation, nameof(command.Operation), 128);
        if (command.ProfileId is not null) Identifier(command.ProfileId, nameof(command.ProfileId), 128);
        if (command.Arguments.ValueKind == JsonValueKind.Undefined)
            throw new ArgumentException("Local command arguments must be a defined JSON value.", nameof(command));
    }

    private static void ValidateMessage<T>(T value)
    {
        switch (value)
        {
            case LocalWorkerEndpoint endpoint: endpoint.Validate(); break;
            case LocalWorkerHello hello:
                if (string.IsNullOrWhiteSpace(hello.Product) || hello.Product.Length > 128 || hello.Product.Any(char.IsControl))
                    throw new ArgumentException("A bounded product identity is required.", nameof(value));
                ValidateInstanceId(hello.InstanceId);
                break;
            case LocalWorkerRequest request: ValidateRequest(request); break;
            case CommandRequest command: ValidateCommand(command); break;
            case CommandResult result when result.Data.ValueKind == JsonValueKind.Undefined:
                throw new ArgumentException("A local command result requires defined JSON data.", nameof(value));
            case ServiceEvent item:
                Identifier(item.Type, nameof(item.Type), 256);
                if (item.JobId is not null) Identifier(item.JobId, nameof(item.JobId), 256);
                if (item.Sequence < 0 || item.Data.ValueKind == JsonValueKind.Undefined)
                    throw new ArgumentException("A local service event requires a nonnegative sequence and defined JSON data.", nameof(value));
                break;
        }
    }

    private static void Identifier(string value, string name, int maximumLength)
    {
        if (string.IsNullOrEmpty(value) || value.Length > maximumLength || value.Any(character => char.IsWhiteSpace(character) || char.IsControl(character)))
            throw new ArgumentException($"{name} must be a bounded identifier without whitespace or control characters.", name);
    }

    private static void ValidateJson(byte[] payload)
    {
        if (payload.Length is <= 0 or > MaximumFrameBytes)
            throw new InvalidDataException("The local Worker JSON message exceeds the 4 MiB frame limit.");
        _ = StrictUtf8.GetCharCount(payload);
        using var document = JsonDocument.Parse(payload, new JsonDocumentOptions { MaxDepth = 32 });
        RejectDuplicates(document.RootElement);
    }

    private static void RejectDuplicates(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new InvalidDataException("A local Worker message contains duplicate JSON properties.");
                RejectDuplicates(property.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var item in value.EnumerateArray()) RejectDuplicates(item);
    }

    private static async Task ReadExactlyAsync(Stream stream, Memory<byte> target, string part, CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < target.Length)
        {
            var read = await stream.ReadAsync(target[offset..], cancellationToken).ConfigureAwait(false);
            if (read == 0) throw new EndOfStreamException($"The local Worker disconnected before the {part} was complete.");
            offset += read;
        }
    }

    private static async Task WriteFrameAsync(Stream stream, byte[] payload, CancellationToken cancellationToken)
    {
        var header = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(header, payload.Length);
        await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private sealed class BoundedBuffer : MemoryStream
    {
        private void Check(int count)
        {
            if (count < 0 || Position > MaximumFrameBytes - count)
                throw new InvalidDataException("The local Worker JSON message exceeds the 4 MiB frame limit.");
        }
        public override void Write(byte[] buffer, int offset, int count) { Check(count); base.Write(buffer, offset, count); }
        public override void Write(ReadOnlySpan<byte> buffer) { Check(buffer.Length); base.Write(buffer); }
        public override void WriteByte(byte value) { Check(1); base.WriteByte(value); }
    }
}
